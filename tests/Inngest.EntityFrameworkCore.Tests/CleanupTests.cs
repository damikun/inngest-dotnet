namespace Inngest.EntityFrameworkCore.Tests;

// Events that Inngest took are kept for a while, so that an id the code chose cannot be added again while Inngest would
// still drop it, and then deleted. Events that are still waiting are never deleted.
public sealed class CleanupTests
{
    [Fact]
    public async Task Sent_events_are_deleted_once_the_retention_time_has_passed_and_not_before()
    {
        using var database = SqliteDatabase.Single();
        await using var host = new OutboxHost(database, options => options.Retention = TimeSpan.FromHours(24));

        foreach (var number in new[] { "A-1", "A-2", "A-3" })
        {
            await host.PlaceOrderAsync(number);
        }

        await host.Processor.DispatchAsync(CancellationToken.None);

        host.Time.Advance(TimeSpan.FromHours(20));
        foreach (var number in new[] { "B-1", "B-2" })
        {
            await host.PlaceOrderAsync(number);
        }

        await host.Processor.DispatchAsync(CancellationToken.None);
        host.Client.Events.Count.ShouldBe(5);

        host.Time.Advance(TimeSpan.FromHours(3) + TimeSpan.FromMinutes(59));
        (await host.Processor.CleanUpAsync(CancellationToken.None)).ShouldBe(0);
        (await host.RowsAsync()).Count.ShouldBe(5);

        host.Time.Advance(TimeSpan.FromMinutes(1));
        (await host.Processor.CleanUpAsync(CancellationToken.None)).ShouldBe(3);
        (await host.RowsAsync()).Count.ShouldBe(2);

        host.Time.Advance(TimeSpan.FromHours(20));
        (await host.Processor.CleanUpAsync(CancellationToken.None)).ShouldBe(2);
        (await host.RowsAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task An_event_that_is_still_waiting_is_never_deleted()
    {
        using var database = SqliteDatabase.Single();
        var client = new RecordingInngestClient { OnSend = (_, _) => throw new InngestException("Inngest API error: 500 Internal server error", 500) };
        await using var host = new OutboxHost(database, options => options.Retention = TimeSpan.Zero, client);
        await host.PlaceOrderAsync("A-1");
        await host.Processor.DispatchAsync(CancellationToken.None);

        host.Time.Advance(TimeSpan.FromDays(400));

        (await host.Processor.CleanUpAsync(CancellationToken.None)).ShouldBe(0);
        (await host.RowsAsync()).ShouldHaveSingleItem().SentAt.ShouldBeNull();
    }

    [Fact]
    public async Task Cleanup_deletes_in_batches_of_the_size_the_options_say()
    {
        using var database = SqliteDatabase.Single();
        await using var host = new OutboxHost(database, options =>
        {
            options.Retention = TimeSpan.FromHours(1);
            options.CleanupBatchSize = 2;
        });
        foreach (var number in new[] { "A-1", "A-2", "A-3", "A-4", "A-5" })
        {
            await host.PlaceOrderAsync(number);
        }

        await host.Processor.DispatchAsync(CancellationToken.None);
        host.Time.Advance(TimeSpan.FromHours(2));

        (await host.Processor.CleanUpAsync(CancellationToken.None)).ShouldBe(5);

        // Two, two, and one that was less than a full batch.
        host.Logs.GetSnapshot().Count(record => record.Message.Contains("DELETE FROM", StringComparison.Ordinal)).ShouldBe(3);
        (await host.RowsAsync()).ShouldBeEmpty();
    }
}
