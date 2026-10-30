using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;

namespace Inngest.IntegrationTests.Outbox;

// The outbox on a PostgreSQL server, where several dispatchers really run at the same time and the isolation of
// transactions is real: that dispatchers share a table without a lock, and see a save only when it commits.
public sealed class PostgresOutboxTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact(Timeout = 120_000)]
    public async Task The_model_creates_the_table_and_a_committed_event_is_sent_once()
    {
        var connectionString = await postgres.CreateDatabaseAsync(Cancellation);
        await using var host = new PostgresOutboxHost(connectionString);
        await host.CreateSchemaAsync(Cancellation);

        var message = await host.PlaceOrderAsync("A-1", Cancellation);
        (await host.Processor.DispatchAsync(Cancellation)).ShouldBe(1);
        (await host.Processor.DispatchAsync(Cancellation)).ShouldBe(0);

        host.Client.Events.ShouldHaveSingleItem().Id.ShouldBe(message.Id);
        var row = (await host.RowsAsync(Cancellation)).ShouldHaveSingleItem();
        row.SentAt.ShouldBe(PostgresOutboxHost.Start.UtcDateTime);
        row.SentAt!.Value.Kind.ShouldBe(DateTimeKind.Utc);
        row.Timestamp.ShouldBe(PostgresOutboxHost.Start.UtcDateTime);
        row.Attempts.ShouldBe(1);
        row.LeaseToken.ShouldBeNull();

        var types = await ColumnTypesAsync(connectionString);
        types["Id"].ShouldBe("character varying");
        types["Data"].ShouldBe("text");
        types["Timestamp"].ShouldBe("timestamp with time zone");
        types["NextAttemptAt"].ShouldBe("timestamp with time zone");
        types["LeaseExpiresAt"].ShouldBe("timestamp with time zone");
        types["SentAt"].ShouldBe("timestamp with time zone");
        types["Attempts"].ShouldBe("integer");
    }

    [Fact(Timeout = 120_000)]
    public async Task A_dispatcher_sees_an_event_only_when_its_transaction_commits()
    {
        var connectionString = await postgres.CreateDatabaseAsync(Cancellation);
        await using var saver = new PostgresOutboxHost(connectionString);
        await using var dispatcher = new PostgresOutboxHost(connectionString);
        await saver.CreateSchemaAsync(Cancellation);

        await using var context = saver.CreateContext();
        await using var transaction = await context.Database.BeginTransactionAsync(Cancellation);
        context.Orders.Add(new ShopOrder { Number = "A-1" });
        context.AddInngestEvent(new InngestEvent("shop/order.placed", new { OrderNumber = "A-1" }));
        await context.SaveChangesAsync(Cancellation);

        // Saved, not committed: a dispatcher that woke up now would find nothing.
        (await dispatcher.Processor.DispatchAsync(Cancellation)).ShouldBe(0);

        await transaction.CommitAsync(Cancellation);

        (await dispatcher.Processor.DispatchAsync(Cancellation)).ShouldBe(1);
        dispatcher.Client.Events.ShouldHaveSingleItem();
    }

    [Fact(Timeout = 120_000)]
    public async Task A_rolled_back_transaction_sends_nothing()
    {
        var connectionString = await postgres.CreateDatabaseAsync(Cancellation);
        await using var host = new PostgresOutboxHost(connectionString);
        await host.CreateSchemaAsync(Cancellation);

        await using (var context = host.CreateContext())
        {
            await using var transaction = await context.Database.BeginTransactionAsync(Cancellation);
            context.Orders.Add(new ShopOrder { Number = "A-1" });
            context.AddInngestEvent(new InngestEvent("shop/order.placed", new { OrderNumber = "A-1" }));
            await context.SaveChangesAsync(Cancellation);
            await transaction.RollbackAsync(Cancellation);
        }

        (await host.Processor.DispatchAsync(Cancellation)).ShouldBe(0);
        host.Client.Requests.ShouldBeEmpty();
        (await host.RowsAsync(Cancellation)).ShouldBeEmpty();
    }

    [Fact(Timeout = 120_000)]
    public async Task Four_dispatchers_on_one_database_send_every_row_exactly_once()
    {
        var connectionString = await postgres.CreateDatabaseAsync(Cancellation);
        var client = new RecordingClient { OnSend = async (_, cancellationToken) => await Task.Delay(2, cancellationToken) };
        var hosts = Enumerable.Range(0, 4).Select(_ => new PostgresOutboxHost(connectionString, options => options.BatchSize = 25, client)).ToList();
        try
        {
            await hosts[0].CreateSchemaAsync(Cancellation);

            const int Count = 800;
            await using (var context = hosts[0].CreateContext())
            {
                for (var i = 0; i < Count; i++)
                {
                    context.AddInngestEvent(new InngestEvent("shop/order.shipped", new { OrderNumber = $"A-{i}" }));
                }

                await context.SaveChangesAsync(Cancellation);
            }

            await Task.WhenAll(hosts.Select(host => Task.Run(() => DrainAsync(host), Cancellation)));

            var ids = client.Events.Select(@event => @event.Id).ToList();
            ids.Count.ShouldBe(Count);
            ids.Distinct().Count().ShouldBe(Count);
            (await hosts[0].RowsAsync(Cancellation)).ShouldAllBe(row => row.SentAt != null && row.Attempts == 1 && row.LeaseToken == null);
        }
        finally
        {
            foreach (var host in hosts)
            {
                await host.DisposeAsync();
            }
        }
    }

    [Fact(Timeout = 120_000)]
    public async Task A_dispatcher_that_chose_its_rows_before_another_took_them_claims_nothing()
    {
        var connectionString = await postgres.CreateDatabaseAsync(Cancellation);
        var client = new RecordingClient();
        var order = new ClaimOrder();
        await using var first = new PostgresOutboxHost(connectionString, client: client, contextOptions: options => options.AddInterceptors(order.First));
        await using var second = new PostgresOutboxHost(connectionString, client: client, contextOptions: options => options.AddInterceptors(order.Second));
        await first.CreateSchemaAsync(Cancellation);
        for (var i = 0; i < 3; i++)
        {
            await first.PlaceOrderAsync($"A-{i}", Cancellation);
        }

        // Both have chosen the same three rows when the first one claims them. The second one then runs its claim, which
        // must not take rows that are no longer due, and must not overwrite the token of the first.
        var claimedByFirst = Task.Run(() => first.Processor.DispatchAsync(Cancellation), Cancellation);
        var claimedBySecond = Task.Run(() => second.Processor.DispatchAsync(Cancellation), Cancellation);

        (await claimedByFirst).ShouldBe(3);
        (await claimedBySecond).ShouldBe(0);
        client.Events.Count.ShouldBe(3);
    }

    [Fact(Timeout = 120_000)]
    public async Task An_expired_lease_is_reclaimed_by_another_dispatcher()
    {
        var connectionString = await postgres.CreateDatabaseAsync(Cancellation);
        await using var host = new PostgresOutboxHost(connectionString);
        await host.CreateSchemaAsync(Cancellation);
        var message = await host.PlaceOrderAsync("A-1", Cancellation);

        // What a dispatcher that died in the middle of a send leaves behind.
        await using (var context = host.CreateContext())
        {
            var until = host.Time.GetUtcNow().UtcDateTime.AddMinutes(1);
            await context.Set<InngestOutboxMessage>()
                .Where(row => row.Id == message.Id)
                .ExecuteUpdateAsync(set => set.SetProperty(row => row.LeaseToken, "a-dead-dispatcher").SetProperty(row => row.LeaseExpiresAt, (DateTime?)until), Cancellation);
        }

        host.Time.Advance(TimeSpan.FromSeconds(59));
        (await host.Processor.DispatchAsync(Cancellation)).ShouldBe(0);

        host.Time.Advance(TimeSpan.FromSeconds(2));
        (await host.Processor.DispatchAsync(Cancellation)).ShouldBe(1);
        host.Client.Events.ShouldHaveSingleItem().Id.ShouldBe(message.Id);
    }

    [Fact(Timeout = 120_000)]
    public async Task Sent_events_are_deleted_in_batches_once_the_retention_time_has_passed()
    {
        var connectionString = await postgres.CreateDatabaseAsync(Cancellation);
        await using var host = new PostgresOutboxHost(connectionString, options =>
        {
            options.Retention = TimeSpan.FromHours(1);
            options.CleanupBatchSize = 2;
        });
        await host.CreateSchemaAsync(Cancellation);
        foreach (var number in new[] { "A-1", "A-2", "A-3", "A-4", "A-5" })
        {
            await host.PlaceOrderAsync(number, Cancellation);
        }

        await host.Processor.DispatchAsync(Cancellation);
        await host.PlaceOrderAsync("B-1", Cancellation);

        host.Time.Advance(TimeSpan.FromMinutes(59));
        (await host.Processor.CleanUpAsync(Cancellation)).ShouldBe(0);

        host.Time.Advance(TimeSpan.FromMinutes(2));
        (await host.Processor.CleanUpAsync(Cancellation)).ShouldBe(5);

        // The event that was never sent stays, however old it is.
        (await host.RowsAsync(Cancellation)).ShouldHaveSingleItem().SentAt.ShouldBeNull();
    }

    private static async Task DrainAsync(PostgresOutboxHost host)
    {
        while (await host.Processor.DispatchAsync(Cancellation) > 0)
        {
        }
    }

    private static async Task<Dictionary<string, string>> ColumnTypesAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(Cancellation);
        await using var command = new NpgsqlCommand("SELECT column_name, data_type FROM information_schema.columns WHERE table_name = 'InngestOutbox'", connection);
        await using var reader = await command.ExecuteReaderAsync(Cancellation);
        var types = new Dictionary<string, string>();
        while (await reader.ReadAsync(Cancellation))
        {
            types[reader.GetString(0)] = reader.GetString(1);
        }

        return types;
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
            private static bool IsChoosing(DbCommand command) =>
                command.CommandText.StartsWith("SELECT", StringComparison.Ordinal) && command.CommandText.Contains("LIMIT", StringComparison.Ordinal);

            private static bool IsReadingClaim(DbCommand command) =>
                command.CommandText.StartsWith("SELECT", StringComparison.Ordinal) && !command.CommandText.Contains("LIMIT", StringComparison.Ordinal);

            // The claim is the only update that sets the lease token to a value; the others clear it.
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
