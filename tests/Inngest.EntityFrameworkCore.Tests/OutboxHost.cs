using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;

namespace Inngest.EntityFrameworkCore.Tests;

// An app with the outbox on an SQLite database, the way an app registers it, and a client that records what is sent in
// place of Inngest. The dispatcher is not started unless a test starts it, so tests drive passes themselves.
internal sealed class OutboxHost : IAsyncDisposable
{
    public static readonly DateTimeOffset Start = new(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);

    private readonly ServiceProvider _provider;

    public OutboxHost(
        SqliteDatabase database,
        Action<InngestOutboxOptions>? configure = null,
        RecordingInngestClient? client = null,
        FakeTimeProvider? time = null,
        bool wakeOnSave = true,
        Action<InngestOptions>? inngest = null,
        Action<DbContextOptionsBuilder>? contextOptions = null)
    {
        Client = client ?? new RecordingInngestClient();
        Time = time ?? new FakeTimeProvider(Start);

        var services = new ServiceCollection();
        services.AddFakeLogging();
        services.AddSingleton(Time);
        services.AddSingleton<TimeProvider>(Time);
        services.AddSingleton<IInngestClient>(Client);
        services.AddInngest(options =>
        {
            options.AppId = "shop";
            options.IsDev = true;
            inngest?.Invoke(options);
        }).AddEntityFrameworkOutbox<ShopContext>(configure);
        services.AddDbContext<ShopContext>((provider, options) =>
        {
            database.Configure(options);
            contextOptions?.Invoke(options);
            if (wakeOnSave)
            {
                options.UseInngestOutbox(provider);
            }
        });
        _provider = services.BuildServiceProvider();

        using var scope = _provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<ShopContext>().Database.EnsureCreated();
    }

    public RecordingInngestClient Client { get; }

    public FakeTimeProvider Time { get; }

    public IServiceProvider Services => _provider;

    public FakeLogCollector Logs => _provider.GetFakeLogCollector();

    public OutboxProcessor<ShopContext> Processor => _provider.GetRequiredService<OutboxProcessor<ShopContext>>();

    public OutboxDispatcher<ShopContext> Dispatcher => _provider.GetServices<IHostedService>().OfType<OutboxDispatcher<ShopContext>>().Single();

    public OutboxSignal Signal => _provider.GetRequiredService<OutboxSignals>().For(typeof(ShopContext));

    // A context of its own in a scope of its own, like the one a request gets. The scope ends with the host.
    public ShopContext CreateContext() => _provider.CreateScope().ServiceProvider.GetRequiredService<ShopContext>();

    // Saves an order and its event the way app code does, in the context's own transaction.
    public async Task<InngestOutboxMessage> PlaceOrderAsync(string number, decimal total = 10)
    {
        await using var context = CreateContext();
        context.Orders.Add(new Order { Number = number, Total = total });
        var message = context.AddInngestEvent(new OrderPlaced(number, total));
        await context.SaveChangesAsync();
        return message;
    }

    // What the table holds now, read around the entity cache.
    public async Task<List<InngestOutboxMessage>> RowsAsync()
    {
        await using var context = CreateContext();
        return await context.Set<InngestOutboxMessage>().AsNoTracking().OrderBy(message => message.Id).ToListAsync();
    }

    public async ValueTask DisposeAsync() => await _provider.DisposeAsync();
}
