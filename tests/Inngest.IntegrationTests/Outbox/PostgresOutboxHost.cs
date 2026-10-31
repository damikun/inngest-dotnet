using Inngest.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Inngest.IntegrationTests.Outbox;

// One instance of an app with the outbox on a PostgreSQL database. Several of them on one database stand in for the
// processes of a deployment: each has its own service container, dispatcher and connections.
internal sealed class PostgresOutboxHost : IAsyncDisposable
{
    public static readonly DateTimeOffset Start = new(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);

    private readonly ServiceProvider _provider;

    public PostgresOutboxHost(
        string connectionString,
        Action<InngestOutboxOptions>? configure = null,
        RecordingClient? client = null,
        FakeTimeProvider? time = null,
        Action<DbContextOptionsBuilder>? contextOptions = null)
    {
        Client = client ?? new RecordingClient();
        Time = time ?? new FakeTimeProvider(Start);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<TimeProvider>(Time);
        services.AddSingleton<IInngestClient>(Client);
        services.AddInngest(options =>
        {
            options.AppId = "shop";
            options.IsDev = true;
        }).AddEntityFrameworkOutbox<ShopContext>(configure);
        services.AddDbContext<ShopContext>(options =>
        {
            options.UseNpgsql(connectionString);
            contextOptions?.Invoke(options);
            options.UseInngestOutbox();
        });
        _provider = services.BuildServiceProvider();
    }

    public RecordingClient Client { get; }

    public FakeTimeProvider Time { get; }

    public OutboxProcessor<ShopContext> Processor => _provider.GetRequiredService<OutboxProcessor<ShopContext>>();

    // What the dispatcher waits on, and the interceptor sets after a save.
    public OutboxSignal Signal => _provider.GetRequiredService<OutboxSignals>().For(typeof(ShopContext));

    // A context of its own in a scope of its own, like the one a request gets.
    public ShopContext CreateContext() => _provider.CreateScope().ServiceProvider.GetRequiredService<ShopContext>();

    public async Task CreateSchemaAsync(CancellationToken cancellationToken)
    {
        await using var context = CreateContext();
        await context.Database.EnsureCreatedAsync(cancellationToken);
    }

    public async Task<InngestOutboxMessage> PlaceOrderAsync(string number, CancellationToken cancellationToken)
    {
        await using var context = CreateContext();
        context.Orders.Add(new ShopOrder { Number = number });
        var message = context.AddInngestEvent(new InngestEvent("shop/order.placed", new { OrderNumber = number }));
        await context.SaveChangesAsync(cancellationToken);
        return message;
    }

    public async Task<List<InngestOutboxMessage>> RowsAsync(CancellationToken cancellationToken)
    {
        await using var context = CreateContext();
        return await context.Set<InngestOutboxMessage>().AsNoTracking().OrderBy(message => message.Id).ToListAsync(cancellationToken);
    }

    public async ValueTask DisposeAsync() => await _provider.DisposeAsync();
}
