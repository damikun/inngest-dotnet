using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

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

    [Theory]
    [InlineData(ContextRegistration.WithProvider)]
    [InlineData(ContextRegistration.WithoutProvider)]
    [InlineData(ContextRegistration.Pooled)]
    public async Task A_save_wakes_the_dispatcher_however_the_options_of_the_context_are_built(ContextRegistration registration)
    {
        using var database = SqliteDatabase.Single();
        await using var host = new OutboxHost(database, registration: registration);

        await host.PlaceOrderAsync("A-1");

        (await WokenAsync(host)).ShouldBeTrue();
    }

    [Fact]
    public async Task UseInngestOutbox_in_OnConfiguring_wakes_it_too()
    {
        using var database = SqliteDatabase.Single();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IInngestClient>(new RecordingInngestClient());
        services.AddInngest(options =>
        {
            options.AppId = "shop";
            options.IsDev = true;
        }).AddEntityFrameworkOutbox<ConfiguringContext>();
        services.AddDbContext<ConfiguringContext>(options => database.Configure(options));
        await using var provider = services.BuildServiceProvider();

        await using (var context = provider.CreateScope().ServiceProvider.GetRequiredService<ConfiguringContext>())
        {
            await context.Database.EnsureCreatedAsync();
            context.AddInngestEvent(new OrderPlaced("A-1", 10));
            await context.SaveChangesAsync();
        }

        var signal = provider.GetRequiredService<OutboxSignals>().For(typeof(ConfiguringContext));
        (await signal.WaitAsync(TimeSpan.FromSeconds(10), TimeProvider.System, CancellationToken.None)).ShouldBeTrue();
    }

    [Fact]
    public async Task UseInngestOutbox_in_two_places_wakes_it_once_and_does_no_harm()
    {
        using var database = SqliteDatabase.Single();
        await using var host = new OutboxHost(database, contextOptions: options => options.UseInngestOutbox());

        await host.PlaceOrderAsync("A-1");

        (await WokenAsync(host)).ShouldBeTrue();
        (await WokenAsync(host, Silence)).ShouldBeFalse();
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
        await using var host = new OutboxHost(database, registration: ContextRegistration.None);

        await host.PlaceOrderAsync("A-1");

        (await WokenAsync(host, Silence)).ShouldBeFalse();
    }

    // True if the signal fires within the time. A signal that is already pending counts, and is used up.
    private static Task<bool> WokenAsync(OutboxHost host, TimeSpan? within = null) =>
        host.Signal.WaitAsync(within ?? TimeSpan.FromSeconds(10), TimeProvider.System, CancellationToken.None);
}
