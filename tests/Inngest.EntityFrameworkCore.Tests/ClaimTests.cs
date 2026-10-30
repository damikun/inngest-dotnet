using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Inngest.EntityFrameworkCore.Tests;

// Dispatchers share the table and no lock: what keeps one event from going out twice is the claim.
public sealed class ClaimTests
{
    [Fact(Timeout = 60_000)]
    public async Task Two_dispatchers_never_send_a_row_twice()
    {
        using var database = SqliteDatabase.Shared();
        var client = new RecordingInngestClient { OnSend = async (_, _) => await Task.Yield() };
        await using var first = new OutboxHost(database, options => options.BatchSize = 7, client);
        await using var second = new OutboxHost(database, options => options.BatchSize = 7, client);

        const int Count = 150;
        await using (var context = first.CreateContext())
        {
            for (var i = 0; i < Count; i++)
            {
                context.AddInngestEvent(new OrderShipped($"A-{i}"));
            }

            await context.SaveChangesAsync();
        }

        var cancellation = TestContext.Current.CancellationToken;
        await Task.WhenAll(Task.Run(() => DrainAsync(first, cancellation), cancellation), Task.Run(() => DrainAsync(second, cancellation), cancellation));

        var ids = client.Events.Select(@event => @event.Id).ToList();
        ids.Count.ShouldBe(Count);
        ids.Distinct().Count().ShouldBe(Count);
        (await first.RowsAsync()).ShouldAllBe(row => row.SentAt != null && row.Attempts == 1 && row.LeaseToken == null);
    }

    [Fact]
    public async Task A_claim_marks_the_rows_with_a_token_and_an_expiry_while_they_are_sent()
    {
        using var database = SqliteDatabase.Single();
        var client = new RecordingInngestClient();
        await using var host = new OutboxHost(database, options => options.LeaseDuration = TimeSpan.FromMinutes(2), client);
        await host.PlaceOrderAsync("A-1");

        List<InngestOutboxMessage>? duringSend = null;
        client.OnSend = async (_, _) => duringSend = await host.RowsAsync();
        await host.Processor.DispatchAsync(CancellationToken.None);

        var row = duringSend.ShouldNotBeNull().ShouldHaveSingleItem();
        row.LeaseToken.ShouldNotBeNullOrEmpty();
        row.LeaseExpiresAt.ShouldBe(OutboxHost.Start.UtcDateTime.AddMinutes(2));
        row.SentAt.ShouldBeNull();
    }

    [Fact]
    public async Task Rows_that_another_dispatcher_holds_are_left_alone_until_its_lease_ends()
    {
        using var database = SqliteDatabase.Single();
        await using var host = new OutboxHost(database);
        var message = await host.PlaceOrderAsync("A-1");

        // What a dispatcher that died in the middle of a send leaves behind.
        await LeaseAsync(host, message.Id, "token-of-a-dead-dispatcher", host.Time.GetUtcNow().UtcDateTime.AddMinutes(1));

        host.Time.Advance(TimeSpan.FromSeconds(59));
        (await host.Processor.DispatchAsync(CancellationToken.None)).ShouldBe(0);
        host.Client.Requests.ShouldBeEmpty();

        host.Time.Advance(TimeSpan.FromSeconds(2));
        (await host.Processor.DispatchAsync(CancellationToken.None)).ShouldBe(1);
        host.Client.Events.ShouldHaveSingleItem().Id.ShouldBe(message.Id);
        (await host.RowsAsync()).ShouldHaveSingleItem().SentAt.ShouldNotBeNull();
    }

    [Fact(Timeout = 30_000)]
    public async Task A_dispatcher_that_lost_its_lease_leaves_the_rows_to_the_one_that_took_them()
    {
        using var database = SqliteDatabase.Single();
        var client = new RecordingInngestClient();
        await using var host = new OutboxHost(database, options => options.LeaseDuration = TimeSpan.FromMinutes(1), client);
        await host.PlaceOrderAsync("A-1");

        // The first dispatcher is stuck in its send for longer than its lease. Another one takes the row, sends it and
        // marks it. Then the first one finishes, and has to see that the row is no longer its own.
        var firstIsSending = new TaskCompletionSource();
        var releaseFirst = new TaskCompletionSource();
        var firstRequest = true;
        client.OnSend = async (_, _) =>
        {
            if (firstRequest)
            {
                firstRequest = false;
                firstIsSending.SetResult();
                await releaseFirst.Task;
            }
        };

        var cancellation = TestContext.Current.CancellationToken;
        var first = Task.Run(() => host.Processor.DispatchAsync(cancellation), cancellation);
        await firstIsSending.Task.WaitAsync(cancellation);

        host.Time.Advance(TimeSpan.FromMinutes(2));
        (await host.Processor.DispatchAsync(cancellation)).ShouldBe(1);
        var markedBySecond = (await host.RowsAsync()).ShouldHaveSingleItem();

        releaseFirst.SetResult();
        await first;

        // Inngest got the event twice, under one id, which it drops; the table says it once.
        client.Events.Select(@event => @event.Id).Distinct().Count().ShouldBe(1);
        var row = (await host.RowsAsync()).ShouldHaveSingleItem();
        row.SentAt.ShouldBe(markedBySecond.SentAt);
        row.Attempts.ShouldBe(1);
    }

    [Fact(Timeout = 30_000)]
    public async Task A_dispatcher_that_chose_its_rows_before_another_took_them_claims_nothing()
    {
        using var database = SqliteDatabase.Shared();
        var client = new RecordingInngestClient();
        var order = new ClaimOrder();
        await using var first = new OutboxHost(database, client: client, contextOptions: options => options.AddInterceptors(order.First));
        await using var second = new OutboxHost(database, client: client, contextOptions: options => options.AddInterceptors(order.Second));
        for (var i = 0; i < 3; i++)
        {
            await first.PlaceOrderAsync($"A-{i}");
        }

        // Both have chosen the same three rows when the first one claims them. The second one then runs its claim, which
        // must not take rows that are no longer due, and must not overwrite the token of the first.
        var cancellation = TestContext.Current.CancellationToken;
        var claimedByFirst = Task.Run(() => first.Processor.DispatchAsync(cancellation), cancellation);
        var claimedBySecond = Task.Run(() => second.Processor.DispatchAsync(cancellation), cancellation);

        (await claimedByFirst).ShouldBe(3);
        (await claimedBySecond).ShouldBe(0);
        client.Events.Count.ShouldBe(3);
    }

    private static async Task DrainAsync(OutboxHost host, CancellationToken cancellationToken)
    {
        while (await host.Processor.DispatchAsync(cancellationToken) > 0)
        {
        }
    }

    private static async Task LeaseAsync(OutboxHost host, string id, string token, DateTime until)
    {
        await using var context = host.CreateContext();
        await context.Set<InngestOutboxMessage>()
            .Where(message => message.Id == id)
            .ExecuteUpdateAsync(set => set
                .SetProperty(message => message.LeaseToken, token)
                .SetProperty(message => message.LeaseExpiresAt, (DateTime?)until));
    }

    // Makes two dispatchers meet in the worst order. Both choose their rows; the first claims and reads what it claimed
    // while the second waits right before its own claim; then the second goes on with the rows it chose earlier.
    private sealed class ClaimOrder
    {
        private readonly TaskCompletionSource _secondChose = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _firstClaimed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ClaimOrder()
        {
            First = new Interceptor(this, isFirst: true);
            Second = new Interceptor(this, isFirst: false);
        }

        public DbCommandInterceptor First { get; }

        public DbCommandInterceptor Second { get; }

        private sealed class Interceptor(ClaimOrder order, bool isFirst) : DbCommandInterceptor
        {
            // The query that chooses the rows has a LIMIT; the one that reads the claimed rows back has not.
            private static bool IsChoosing(DbCommand command) => command.CommandText.StartsWith("SELECT", StringComparison.Ordinal) && command.CommandText.Contains("LIMIT", StringComparison.Ordinal);

            private static bool IsReadingClaim(DbCommand command) => command.CommandText.StartsWith("SELECT", StringComparison.Ordinal) && !command.CommandText.Contains("LIMIT", StringComparison.Ordinal);

            // The claim is the only update that sets the lease token to a value; the others clear it. EF Core 8 and 10 list the
            // assignments in different orders, so the whole SET clause is looked at.
            private static bool IsClaiming(DbCommand command) =>
                command.CommandText.StartsWith("UPDATE", StringComparison.Ordinal)
                && command.CommandText[..command.CommandText.IndexOf("WHERE", StringComparison.Ordinal)].Contains("\"LeaseToken\" = @", StringComparison.Ordinal);

            public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
            {
                if (IsClaiming(command))
                {
                    await (isFirst ? order._secondChose.Task : order._firstClaimed.Task).WaitAsync(TimeSpan.FromSeconds(20), cancellationToken);
                }

                return result;
            }

            public override ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData, DbDataReader result, CancellationToken cancellationToken = default)
            {
                if (!isFirst && IsChoosing(command))
                {
                    order._secondChose.TrySetResult();
                }
                else if (isFirst && IsReadingClaim(command))
                {
                    order._firstClaimed.TrySetResult();
                }

                return ValueTask.FromResult(result);
            }
        }
    }
}
