using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace Inngest.EntityFrameworkCore.Tests;

// The hosted dispatcher: what keeps passes coming while the app runs. Time is the fake clock's, so the poll interval,
// the retry delays and the cleanup interval pass when a test says so. The tests that wait for the dispatcher keep
// moving the clock while they wait, because they cannot know when it starts to wait on it.
public sealed class DispatcherTests
{
    [Fact(Timeout = 60_000)]
    public async Task A_save_wakes_the_running_dispatcher_which_sends_at_once()
    {
        using var database = SqliteDatabase.Shared();

        // The poll is an hour away on a clock that does not move, so only the wake-up can explain a send.
        await using var host = new OutboxHost(database, options => options.PollInterval = TimeSpan.FromHours(1));
        await using var running = await RunAsync(host);

        await host.PlaceOrderAsync("A-1");

        await Eventually(() => host.Client.Events.Count == 1, TestContext.Current.CancellationToken);
        host.Client.Events.ShouldHaveSingleItem().Name.ShouldBe("shop/order.placed");
    }

    [Fact(Timeout = 60_000)]
    public async Task The_poll_finds_events_that_nothing_woke_the_dispatcher_for()
    {
        using var database = SqliteDatabase.Shared();
        await using var host = new OutboxHost(database, options => options.PollInterval = TimeSpan.FromSeconds(30), registration: ContextRegistration.None);
        await using var running = await RunAsync(host);

        await host.PlaceOrderAsync("A-1");

        await Eventually(() => host.Client.Events.Count == 1, TestContext.Current.CancellationToken, advance: TimeSpan.FromSeconds(31), host.Time);
    }

    [Fact(Timeout = 60_000)]
    public async Task A_failed_event_is_sent_again_when_its_delay_has_passed()
    {
        using var database = SqliteDatabase.Shared();
        var client = new RecordingInngestClient();
        var failures = 2;
        client.OnSend = (_, _) => failures-- > 0 ? throw new InngestException("Inngest API error: 502 Bad gateway", 502) : Task.CompletedTask;
        await using var host = new OutboxHost(database, options => options.RetryDelay = TimeSpan.FromSeconds(5), client);
        await using var running = await RunAsync(host);

        await host.PlaceOrderAsync("A-1");

        await Eventually(() => host.Client.Events.Count == 1, TestContext.Current.CancellationToken, advance: TimeSpan.FromSeconds(6), host.Time);

        // The client has the event before the dispatcher has marked the row, so the row is waited for as well.
        await Eventually(() => host.RowsAsync().GetAwaiter().GetResult() is [{ SentAt: not null }], TestContext.Current.CancellationToken);
        (await host.RowsAsync()).ShouldHaveSingleItem().Attempts.ShouldBe(3);
    }

    [Fact(Timeout = 60_000)]
    public async Task A_database_that_is_not_ready_does_not_stop_the_dispatcher()
    {
        using var database = SqliteDatabase.Shared();
        await using var host = new OutboxHost(database);

        // The app is up before its migration ran: the table is not there yet.
        await using var admin = host.CreateContext();
        await admin.Database.ExecuteSqlRawAsync("ALTER TABLE InngestOutbox RENAME TO Waiting");
        await using var running = await RunAsync(host);

        await Eventually(() => host.Logs.GetSnapshot().Any(record => record.Level == LogLevel.Error && record.Message.Contains("could not read or update", StringComparison.Ordinal)), TestContext.Current.CancellationToken);

        await admin.Database.ExecuteSqlRawAsync("ALTER TABLE Waiting RENAME TO InngestOutbox");
        await host.PlaceOrderAsync("A-1");

        await Eventually(() => host.Client.Events.Count == 1, TestContext.Current.CancellationToken, advance: TimeSpan.FromSeconds(10), host.Time);
    }

    [Fact(Timeout = 60_000)]
    public async Task Sent_events_are_deleted_at_the_cleanup_interval()
    {
        using var database = SqliteDatabase.Shared();
        await using var host = new OutboxHost(database, options =>
        {
            options.Retention = TimeSpan.FromMinutes(10);
            options.CleanupInterval = TimeSpan.FromMinutes(1);
        });
        await using var running = await RunAsync(host);

        await host.PlaceOrderAsync("A-1");
        await Eventually(() => host.Client.Events.Count == 1, TestContext.Current.CancellationToken);

        // Moves the clock by the interval, so the delay between two cleanups is over each time, until the row is old enough.
        await Eventually(() => host.RowsAsync().GetAwaiter().GetResult().Count == 0, TestContext.Current.CancellationToken, advance: TimeSpan.FromMinutes(1), host.Time);
    }

    [Fact]
    public async Task A_dispatcher_that_is_turned_off_sends_nothing()
    {
        using var database = SqliteDatabase.Shared();
        await using var host = new OutboxHost(database, options => options.DispatcherEnabled = false);

        await host.Dispatcher.StartAsync(CancellationToken.None);
        await host.PlaceOrderAsync("A-1");
        host.Time.Advance(TimeSpan.FromHours(1));

        // The host does not wait for ExecuteAsync to get going, so the dispatcher says it is off a moment after it started.
        await Eventually(() => host.Logs.GetSnapshot().Any(record => record.Message.Contains("does not send events in this process", StringComparison.Ordinal)), TestContext.Current.CancellationToken);
        await host.Dispatcher.StopAsync(CancellationToken.None);

        host.Client.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_context_set_up_without_UseInngestOutbox_is_said_to_have_no_wake_ups_when_the_dispatcher_starts()
    {
        using var withoutDatabase = SqliteDatabase.Single();
        using var withDatabase = SqliteDatabase.Single();
        await using var without = new OutboxHost(withoutDatabase, registration: ContextRegistration.None);
        await using var with = new OutboxHost(withDatabase, registration: ContextRegistration.WithoutProvider);

        await without.Dispatcher.StartAsync(CancellationToken.None);
        await with.Dispatcher.StartAsync(CancellationToken.None);
        await without.Dispatcher.StopAsync(CancellationToken.None);
        await with.Dispatcher.StopAsync(CancellationToken.None);

        // Said once, at the level of information, since nothing is lost by it: the events are found at the next poll.
        var said = without.Logs.GetSnapshot().Where(record => record.Message.Contains("is not set up with UseInngestOutbox", StringComparison.Ordinal)).ToList();
        said.ShouldHaveSingleItem().Level.ShouldBe(LogLevel.Information);
        with.Logs.GetSnapshot().ShouldNotContain(record => record.Message.Contains("UseInngestOutbox", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Starting_fails_when_the_context_does_not_map_the_outbox_table()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IInngestClient>(new RecordingInngestClient());
        services.AddInngest(options =>
        {
            options.AppId = "shop";
            options.IsDev = true;
        }).AddEntityFrameworkOutbox<PlainContext>();
        services.AddDbContext<PlainContext>(options => options.UseSqlite(connection));
        await using var provider = services.BuildServiceProvider();

        var dispatcher = provider.GetServices<IHostedService>().OfType<OutboxDispatcher<PlainContext>>().Single();
        var exception = await Should.ThrowAsync<InvalidOperationException>(() => dispatcher.StartAsync(CancellationToken.None));

        exception.Message.ShouldContain("PlainContext");
        exception.Message.ShouldContain("AddInngestOutbox()");
    }

    [Fact(Timeout = 60_000)]
    public async Task Stopping_ends_the_dispatcher_even_while_it_waits()
    {
        using var database = SqliteDatabase.Shared();
        await using var host = new OutboxHost(database);
        await host.Dispatcher.StartAsync(CancellationToken.None);

        await host.Dispatcher.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
    }

    private static async Task<Running> RunAsync(OutboxHost host)
    {
        await host.Dispatcher.StartAsync(CancellationToken.None);
        return new Running(host.Dispatcher);
    }

    // Checks the condition until it holds, with a real clock. Each round it moves the host's clock on by `advance` when
    // given one.
    private static async Task Eventually(Func<bool> condition, CancellationToken cancellationToken, TimeSpan? advance = null, FakeTimeProvider? time = null)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("The condition did not hold within 30 seconds.");
            }

            if (advance is { } step)
            {
                time!.Advance(step);
            }

            await Task.Delay(20, cancellationToken);
        }
    }

    private sealed class Running(OutboxDispatcher<ShopContext> dispatcher) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() => await dispatcher.StopAsync(CancellationToken.None);
    }
}
