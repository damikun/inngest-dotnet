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
    public async Task An_event_that_Inngest_refuses_does_not_hold_back_the_others_in_its_request()
    {
        using var database = SqliteDatabase.Single();
        var client = new RecordingInngestClient
        {
            OnSend = (batch, _) => batch.Any(@event => @event.Name == OrderShipped.EventName)
                ? throw new InngestException("Inngest API error: 413 Event payload too large", 413)
                : Task.CompletedTask,
        };
        await using var host = new OutboxHost(database, client: client);

        string refusedId;
        await using (var context = host.CreateContext())
        {
            context.AddInngestEvent(new OrderPlaced("A-1", 10));
            refusedId = context.AddInngestEvent(new OrderShipped("A-1")).Id;
            context.AddInngestEvent(new OrderPlaced("A-2", 10));
            await context.SaveChangesAsync();
        }

        (await host.Processor.DispatchAsync(CancellationToken.None)).ShouldBe(3);

        // The request with all three was turned down, so each event got one of its own, and only one of them failed.
        client.Requests.Select(request => request.Count).ShouldBe([1, 1]);
        client.Events.ShouldAllBe(@event => @event.Name == OrderPlaced.EventName);

        var rows = await host.RowsAsync();
        rows.Count(row => row.SentAt != null).ShouldBe(2);
        var refused = rows.Single(row => row.SentAt == null);
        refused.Id.ShouldBe(refusedId);
        refused.Attempts.ShouldBe(1);
        refused.LastError.ShouldNotBeNull().ShouldContain("413");
        refused.NextAttemptAt.ShouldBe(OutboxHost.Start.UtcDateTime.AddSeconds(5));

        // It is not dropped quietly: someone has to fix it, and the log says which event it is.
        host.Logs.GetSnapshot().ShouldContain(record => record.Level == LogLevel.Error && record.Message.Contains(refusedId, StringComparison.Ordinal));
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
}
