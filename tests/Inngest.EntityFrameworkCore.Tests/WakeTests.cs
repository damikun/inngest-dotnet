using System.Diagnostics;
using Microsoft.EntityFrameworkCore;

namespace Inngest.EntityFrameworkCore.Tests;

// A save that added events wakes the dispatcher once they are committed: right after the save when it commits by itself,
// and after the commit when the code opened a transaction around it. The tests listen at the signal the dispatcher waits
// on, which is all the interceptor touches.
public sealed class WakeTests
{
    private static readonly TimeSpan Silence = TimeSpan.FromMilliseconds(300);

    [Fact]
    public async Task A_save_that_adds_an_event_wakes_the_dispatcher()
    {
        using var database = SqliteDatabase.Single();
        await using var host = new OutboxHost(database);

        await host.PlaceOrderAsync("A-1");

        (await WokenAsync(host)).ShouldBeTrue();
    }

    [Fact]
    public async Task A_synchronous_save_wakes_it_too()
    {
        using var database = SqliteDatabase.Single();
        await using var host = new OutboxHost(database);

        using (var context = host.CreateContext())
        {
            context.AddInngestEvent(new OrderPlaced("A-1", 10));
            context.SaveChanges();
        }

        (await WokenAsync(host)).ShouldBeTrue();
    }

    [Fact]
    public async Task A_save_in_a_transaction_wakes_it_with_the_commit_and_not_before()
    {
        using var database = SqliteDatabase.Single();
        await using var host = new OutboxHost(database);

        await using var context = host.CreateContext();
        await using var transaction = await context.Database.BeginTransactionAsync();
        context.Orders.Add(new Order { Number = "A-1" });
        context.AddInngestEvent(new OrderPlaced("A-1", 10));
        await context.SaveChangesAsync();

        // The dispatcher would find nothing yet, so it is left to sleep.
        (await WokenAsync(host, Silence)).ShouldBeFalse();

        await transaction.CommitAsync();

        (await WokenAsync(host)).ShouldBeTrue();
    }

    [Fact]
    public async Task Several_saves_in_one_transaction_wake_it_once_at_the_commit()
    {
        using var database = SqliteDatabase.Single();
        await using var host = new OutboxHost(database);

        await using var context = host.CreateContext();
        await using var transaction = await context.Database.BeginTransactionAsync();
        context.AddInngestEvent(new OrderPlaced("A-1", 10));
        await context.SaveChangesAsync();
        context.Orders.Add(new Order { Number = "A-2" });
        await context.SaveChangesAsync();
        (await WokenAsync(host, Silence)).ShouldBeFalse();

        await transaction.CommitAsync();

        (await WokenAsync(host)).ShouldBeTrue();
        (await WokenAsync(host, Silence)).ShouldBeFalse();
    }

    [Fact]
    public async Task A_rolled_back_transaction_never_wakes_it()
    {
        using var database = SqliteDatabase.Single();
        await using var host = new OutboxHost(database);

        await using var context = host.CreateContext();
        await using (var transaction = await context.Database.BeginTransactionAsync())
        {
            context.AddInngestEvent(new OrderPlaced("A-1", 10));
            await context.SaveChangesAsync();
            await transaction.RollbackAsync();
        }

        // Nor does the next transaction of the same context, which holds none of the events.
        await using (var transaction = await context.Database.BeginTransactionAsync())
        {
            context.Orders.Add(new Order { Number = "A-2" });
            await context.SaveChangesAsync();
            await transaction.CommitAsync();
        }

        (await WokenAsync(host, Silence)).ShouldBeFalse();
    }

    [Fact]
    public async Task A_save_without_events_does_not_wake_it()
    {
        using var database = SqliteDatabase.Single();
        await using var host = new OutboxHost(database);

        await using (var context = host.CreateContext())
        {
            context.Orders.Add(new Order { Number = "A-1" });
            await context.SaveChangesAsync();
        }

        (await WokenAsync(host, Silence)).ShouldBeFalse();
    }

    [Fact]
    public async Task A_save_that_fails_does_not_wake_it_and_the_retry_does()
    {
        using var database = SqliteDatabase.Single();
        await using var host = new OutboxHost(database);
        await host.PlaceOrderAsync("A-1");
        (await WokenAsync(host)).ShouldBeTrue();

        await using var context = host.CreateContext();
        var duplicate = new Order { Number = "A-1" };
        context.Orders.Add(duplicate);
        context.AddInngestEvent(new OrderPlaced("A-1", 10));
        await Should.ThrowAsync<DbUpdateException>(() => context.SaveChangesAsync());
        (await WokenAsync(host, Silence)).ShouldBeFalse();

        duplicate.Number = "A-2";
        await context.SaveChangesAsync();

        (await WokenAsync(host)).ShouldBeTrue();
    }

    [Fact]
    public async Task Without_UseInngestOutbox_nothing_wakes_it_and_the_poll_has_to_do()
    {
        using var database = SqliteDatabase.Single();
        await using var host = new OutboxHost(database, wakeOnSave: false);

        await host.PlaceOrderAsync("A-1");

        (await WokenAsync(host, Silence)).ShouldBeFalse();
    }

    // True if the signal fires within the time. A signal that is already pending counts, and is used up.
    private static async Task<bool> WokenAsync(OutboxHost host, TimeSpan? within = null)
    {
        var limit = within ?? TimeSpan.FromSeconds(10);
        var stopwatch = Stopwatch.StartNew();
        await host.Signal.WaitAsync(limit, TimeProvider.System, CancellationToken.None);
        return stopwatch.Elapsed < limit;
    }
}
