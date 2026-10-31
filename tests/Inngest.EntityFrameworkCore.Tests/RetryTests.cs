using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Inngest.EntityFrameworkCore.Tests;

// An event is never dropped: when Inngest does not take it, it stays and is tried again, a little later each time.
public sealed class RetryTests
{
    [Fact]
    public async Task A_failed_send_is_retried_after_a_delay_that_doubles_up_to_the_cap()
    {
        using var database = SqliteDatabase.Single();
        var client = new RecordingInngestClient { OnSend = (_, _) => throw new InngestException("Inngest API error: 500 Internal server error", 500) };
        await using var host = new OutboxHost(
            database,
            options =>
            {
                options.RetryDelay = TimeSpan.FromSeconds(10);
                options.MaxRetryDelay = TimeSpan.FromSeconds(60);
            },
            client);
        var message = await host.PlaceOrderAsync("A-1");

        var delays = new[] { 10, 20, 40, 60, 60, 60 };
        for (var attempt = 1; attempt <= delays.Length; attempt++)
        {
            (await host.Processor.DispatchAsync(CancellationToken.None)).ShouldBe(1);

            var row = (await host.RowsAsync()).ShouldHaveSingleItem();
            row.Attempts.ShouldBe(attempt);
            row.SentAt.ShouldBeNull();
            row.LeaseToken.ShouldBeNull();
            row.LeaseExpiresAt.ShouldBeNull();
            row.LastError.ShouldNotBeNull().ShouldContain("500");
            row.NextAttemptAt.ShouldBe(host.Time.GetUtcNow().UtcDateTime.AddSeconds(delays[attempt - 1]));

            // Not before its time, and then right away.
            host.Time.Advance(TimeSpan.FromSeconds(delays[attempt - 1] - 1));
            (await host.Processor.DispatchAsync(CancellationToken.None)).ShouldBe(0);
            host.Time.Advance(TimeSpan.FromSeconds(1));
        }

        client.OnSend = null;
        (await host.Processor.DispatchAsync(CancellationToken.None)).ShouldBe(1);

        client.Events.ShouldHaveSingleItem().Id.ShouldBe(message.Id);
        var sent = (await host.RowsAsync()).ShouldHaveSingleItem();
        sent.SentAt.ShouldNotBeNull();
        sent.Attempts.ShouldBe(delays.Length + 1);
        sent.LastError.ShouldBeNull();
    }

    [Fact]
    public async Task The_events_of_a_failed_request_fail_together_and_are_not_split()
    {
        using var database = SqliteDatabase.Single();
        var requests = 0;
        var client = new RecordingInngestClient
        {
            OnSend = (_, _) =>
            {
                requests++;
                throw new InngestException("Inngest API error: 503 Service unavailable", 503);
            },
        };
        await using var host = new OutboxHost(database, client: client);
        foreach (var number in new[] { "A-1", "A-2", "A-3" })
        {
            await host.PlaceOrderAsync(number);
        }

        (await host.Processor.DispatchAsync(CancellationToken.None)).ShouldBe(3);

        // Sending them one by one would triple the traffic to a service that is down.
        requests.ShouldBe(1);
        (await host.RowsAsync()).ShouldAllBe(row => row.Attempts == 1 && row.LastError != null && row.SentAt == null);
        host.Logs.GetSnapshot().Where(record => record.Level == LogLevel.Warning).ShouldContain(record => record.Message.Contains("503", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Events_that_Inngest_refuses_do_not_hold_back_the_others_in_their_request_and_each_is_logged_with_its_id()
    {
        using var database = SqliteDatabase.Single();
        var client = new RecordingInngestClient
        {
            OnSend = (batch, _) => batch.Any(@event => @event.Name == OrderShipped.EventName)
                ? throw new InngestException("Inngest API error: 413 Event payload too large", 413)
                : Task.CompletedTask,
        };
        await using var host = new OutboxHost(database, client: client);

        var refusedIds = new List<string>();
        await using (var context = host.CreateContext())
        {
            context.AddInngestEvent(new OrderPlaced("A-1", 10));
            refusedIds.Add(context.AddInngestEvent(new OrderShipped("A-1")).Id);
            context.AddInngestEvent(new OrderPlaced("A-2", 10));
            refusedIds.Add(context.AddInngestEvent(new OrderShipped("A-2")).Id);
            await context.SaveChangesAsync();
        }

        (await host.Processor.DispatchAsync(CancellationToken.None)).ShouldBe(4);

        // The request with all four was turned down, so each event got one of its own, and two of them failed.
        client.Requests.Select(request => request.Count).ShouldBe([1, 1]);
        client.Events.ShouldAllBe(@event => @event.Name == OrderPlaced.EventName);

        var rows = await host.RowsAsync();
        rows.Count(row => row.SentAt != null).ShouldBe(2);
        var refused = rows.Where(row => row.SentAt == null).ToList();
        refused.Select(row => row.Id).ShouldBe(refusedIds, ignoreOrder: true);
        refused.ShouldAllBe(row => row.Attempts == 1 && row.LastError!.Contains("413", StringComparison.Ordinal) && row.NextAttemptAt == OutboxHost.Start.UtcDateTime.AddSeconds(5));

        // They are not dropped quietly: someone has to fix them, and the log names each, though both failed in the same way.
        var errors = host.Logs.GetSnapshot().Where(record => record.Level == LogLevel.Error).ToList();
        errors.Count.ShouldBe(2);
        foreach (var id in refusedIds)
        {
            errors.ShouldContain(record => record.Message.Contains(id, StringComparison.Ordinal) && record.Message.Contains(OrderShipped.EventName, StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task Events_are_sent_one_at_a_time_only_while_the_lease_covers_another_request_and_the_rest_go_back()
    {
        using var database = SqliteDatabase.Single();
        var client = new RecordingInngestClient();
        await using var host = new OutboxHost(
            database,
            options =>
            {
                options.LeaseDuration = TimeSpan.FromSeconds(40);
                options.SendTimeout = TimeSpan.FromSeconds(20);
            },
            client);
        foreach (var number in new[] { "A-1", "A-2", "A-3" })
        {
            await host.PlaceOrderAsync(number);
        }

        // The request with all three is refused. The first request of its own takes 25 seconds, which leaves 15 of the 40
        // of the lease: less than another request may take, so the dispatcher stops there.
        client.OnSend = (batch, _) =>
        {
            if (batch.Count > 1)
            {
                throw new InngestException("Inngest API error: 413 Event payload too large", 413);
            }

            if (client.Events.Count == 0)
            {
                host.Time.Advance(TimeSpan.FromSeconds(25));
            }

            return Task.CompletedTask;
        };

        (await host.Processor.DispatchAsync(CancellationToken.None)).ShouldBe(3);

        var rows = await host.RowsAsync();
        rows.Count(row => row.SentAt != null).ShouldBe(1);
        var returned = rows.Where(row => row.SentAt == null).ToList();
        returned.Count.ShouldBe(2);
        returned.ShouldAllBe(row => row.Attempts == 0 && row.LeaseToken == null && row.LastError == null);

        // They are due at once, and the next pass sends them, as the clock no longer moves.
        client.OnSend = (batch, _) => batch.Count > 1 ? throw new InngestException("Inngest API error: 413 Event payload too large", 413) : Task.CompletedTask;
        (await host.Processor.DispatchAsync(CancellationToken.None)).ShouldBe(2);
        (await host.RowsAsync()).ShouldAllBe(row => row.SentAt != null);
    }

    [Fact]
    public async Task Every_pass_decides_an_event_even_when_a_refused_request_used_up_most_of_the_lease()
    {
        using var database = SqliteDatabase.Single();
        var client = new RecordingInngestClient();

        // The least the options allow. Without the first request of its own, which is sent whatever the lease says, a pass
        // like the ones below would give every event back and the next pass would be the same, for ever.
        await using var host = new OutboxHost(
            database,
            options =>
            {
                options.SendTimeout = TimeSpan.FromSeconds(2);
                options.LeaseDuration = TimeSpan.FromSeconds(4);
            },
            client);
        foreach (var number in new[] { "A-1", "A-2", "A-3" })
        {
            await host.PlaceOrderAsync(number);
            host.Time.Advance(TimeSpan.FromMilliseconds(1));
        }

        // A request with more than one event is refused after 3 seconds. A-2 is an event Inngest refuses on its own as well.
        client.OnSend = (batch, _) =>
        {
            if (batch.Count > 1)
            {
                host.Time.Advance(TimeSpan.FromSeconds(3));
                throw new InngestException("Inngest API error: 413 Event payload too large", 413);
            }

            return OrderNumber(batch[0]) == "A-2" ? throw new InngestException("Inngest API error: 413 Event payload too large", 413) : Task.CompletedTask;
        };

        // The first pass has time for one request of its own: A-1 goes out, and the other two go back untouched.
        (await host.Processor.DispatchAsync(CancellationToken.None)).ShouldBe(3);
        var rows = (await host.RowsAsync()).ToDictionary(row => OrderNumber(row));
        rows["A-1"].SentAt.ShouldNotBeNull();
        rows["A-2"].Attempts.ShouldBe(0);
        rows["A-3"].Attempts.ShouldBe(0);
        rows["A-3"].LeaseToken.ShouldBeNull();

        // The second decides A-2, which fails and waits, and gives A-3 back.
        (await host.Processor.DispatchAsync(CancellationToken.None)).ShouldBe(2);
        rows = (await host.RowsAsync()).ToDictionary(row => OrderNumber(row));
        rows["A-2"].Attempts.ShouldBe(1);
        rows["A-2"].LastError.ShouldNotBeNull();
        rows["A-3"].SentAt.ShouldBeNull();

        // The third has only A-3 left to send, since A-2 waits.
        (await host.Processor.DispatchAsync(CancellationToken.None)).ShouldBe(1);
        rows = (await host.RowsAsync()).ToDictionary(row => OrderNumber(row));
        rows["A-3"].SentAt.ShouldNotBeNull();
        (await host.Processor.DispatchAsync(CancellationToken.None)).ShouldBe(0);
    }

    [Fact]
    public async Task A_refusal_of_a_single_event_is_retried_like_any_failure()
    {
        using var database = SqliteDatabase.Single();
        var client = new RecordingInngestClient { OnSend = (_, _) => throw new InngestException("Inngest API error: 400 Cannot process event payload", 400) };
        await using var host = new OutboxHost(database, client: client);
        await host.PlaceOrderAsync("A-1");

        await host.Processor.DispatchAsync(CancellationToken.None);
        host.Time.Advance(TimeSpan.FromMinutes(10));
        client.OnSend = null;
        await host.Processor.DispatchAsync(CancellationToken.None);

        var row = (await host.RowsAsync()).ShouldHaveSingleItem();
        row.SentAt.ShouldNotBeNull();
        row.Attempts.ShouldBe(2);
    }

    [Fact(Timeout = 30_000)]
    public async Task A_send_that_takes_longer_than_the_timeout_fails_and_is_retried()
    {
        using var database = SqliteDatabase.Single();
        var client = new RecordingInngestClient();
        await using var host = new OutboxHost(database, options => options.SendTimeout = TimeSpan.FromSeconds(20), client);
        await host.PlaceOrderAsync("A-1");

        var sending = new TaskCompletionSource();
        client.OnSend = async (_, cancellationToken) =>
        {
            sending.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
        };

        var cancellation = TestContext.Current.CancellationToken;
        var pass = Task.Run(() => host.Processor.DispatchAsync(cancellation), cancellation);
        await sending.Task.WaitAsync(cancellation);
        host.Time.Advance(TimeSpan.FromSeconds(21));
        (await pass).ShouldBe(1);

        var row = (await host.RowsAsync()).ShouldHaveSingleItem();
        row.Attempts.ShouldBe(1);
        row.SentAt.ShouldBeNull();
        row.LeaseToken.ShouldBeNull();
        row.LastError.ShouldNotBeNull().ShouldContain("did not take the events within");
    }

    [Fact(Timeout = 30_000)]
    public async Task Stopping_gives_the_claimed_events_back_without_counting_an_attempt()
    {
        using var database = SqliteDatabase.Single();
        var client = new RecordingInngestClient();
        await using var host = new OutboxHost(database, client: client);
        await host.PlaceOrderAsync("A-1");

        var sending = new TaskCompletionSource();
        client.OnSend = async (_, cancellationToken) =>
        {
            sending.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
        };

        using var stop = new CancellationTokenSource();
        var cancellation = TestContext.Current.CancellationToken;
        var pass = Task.Run(() => host.Processor.DispatchAsync(stop.Token), cancellation);
        await sending.Task.WaitAsync(cancellation);
        await stop.CancelAsync();
        await Should.ThrowAsync<OperationCanceledException>(() => pass);

        var row = (await host.RowsAsync()).ShouldHaveSingleItem();
        row.LeaseToken.ShouldBeNull();
        row.LeaseExpiresAt.ShouldBeNull();
        row.Attempts.ShouldBe(0);
        row.NextAttemptAt.ShouldBe(OutboxHost.Start.UtcDateTime);

        // Another dispatcher takes it at once, without waiting for any lease.
        client.OnSend = null;
        (await host.Processor.DispatchAsync(cancellation)).ShouldBe(1);
        client.Events.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task The_retry_delay_is_capped_whatever_the_number_of_attempts()
    {
        var settings = new InngestOutboxOptions { RetryDelay = TimeSpan.FromSeconds(5), MaxRetryDelay = TimeSpan.FromMinutes(5) };

        OutboxProcessor<ShopContext>.RetryDelayFor(settings, 1).ShouldBe(TimeSpan.FromSeconds(5));
        OutboxProcessor<ShopContext>.RetryDelayFor(settings, 2).ShouldBe(TimeSpan.FromSeconds(10));
        OutboxProcessor<ShopContext>.RetryDelayFor(settings, 7).ShouldBe(TimeSpan.FromMinutes(5));
        OutboxProcessor<ShopContext>.RetryDelayFor(settings, 10_000).ShouldBe(TimeSpan.FromMinutes(5));
        await Task.CompletedTask;
    }

    private static string OrderNumber(InngestEvent @event) => ((JsonElement)@event.Data!).GetProperty("orderNumber").GetString()!;

    private static string OrderNumber(InngestOutboxMessage row) => JsonDocument.Parse(row.Data).RootElement.GetProperty("orderNumber").GetString()!;
}
