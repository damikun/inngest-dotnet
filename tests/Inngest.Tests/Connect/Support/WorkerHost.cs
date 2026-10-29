using Inngest.Connect;
using Inngest.Execution;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;

namespace Inngest.Tests.Connect.Support;

// The Connect worker as AddConnect registers it, pointed at a FakeGateway, with fake time and fake logging. The
// app is "shop" with one function, "orders", unless a test registers its own.
internal sealed class WorkerHost : IAsyncDisposable
{
    public const string AppId = "shop";
    public const string InstanceId = "worker-1";

    public static readonly DateTimeOffset Now = new(2026, 10, 29, 12, 0, 0, TimeSpan.Zero);

    public static readonly FunctionOptions Orders = new() { Id = "orders", Triggers = [Trigger.Event("order/created")] };

    private readonly ServiceProvider _provider;
    private bool _started;

    public WorkerHost(
        FakeGateway gateway,
        Action<InngestOptions>? inngest = null,
        Action<ConnectOptions>? connect = null,
        Action<IInngestBuilder>? functions = null,
        IExecutionEngine? engine = null)
    {
        var services = new ServiceCollection();

        // An empty configuration keeps INNGEST_* variables of the machine running the tests out of the settings.
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddFakeLogging();
        services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.Debug));
        services.AddSingleton<TimeProvider>(Time);

        var builder = services.AddInngest(options =>
        {
            options.AppId = AppId;
            options.IsDev = true;
            options.DevServerUrl = gateway.BaseUrl;
            inngest?.Invoke(options);
        });

        if (functions is null)
        {
            builder.AddFunction(Orders, static (context, _) => Task.FromResult<object?>(new { received = context.Event.Data.GetProperty("orderId").GetInt32() }));
        }
        else
        {
            functions(builder);
        }

        builder.AddConnect(options =>
        {
            options.InstanceId = InstanceId;
            connect?.Invoke(options);
        });

        if (engine is not null)
        {
            services.Replace(ServiceDescriptor.Singleton(engine));
        }

        _provider = services.BuildServiceProvider();
        Logs = _provider.GetFakeLogCollector();
    }

    public TestTime Time { get; } = new(Now);

    public FakeLogCollector Logs { get; }

    public IServiceProvider Services => _provider;

    public IHostedService Worker => _provider.GetServices<IHostedService>().OfType<ConnectWorker>().Single();

    public async Task StartAsync()
    {
        _started = true;
        await Worker.StartAsync(TestContext.Current.CancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken = default) => Worker.StopAsync(cancellationToken);

    // Moves fake time on in small steps until the condition holds, for waits whose timer the worker starts at a moment
    // the test cannot see, such as a backoff before the next attempt.
    public async Task AdvanceUntilAsync(Func<bool> condition, TimeSpan step)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(Waits.Timeout);
        while (!condition())
        {
            Time.Advance(step);
            await Task.Delay(20, timeout.Token);
        }
    }

    public IReadOnlyList<FakeLogRecord> LogsWithId(int eventId) =>
        [.. Logs.GetSnapshot().Where(record => record.Id.Id == eventId)];

    public async ValueTask DisposeAsync()
    {
        if (_started)
        {
            // Stops at once: the host gave up waiting before it asked.
            using var stopped = new CancellationTokenSource();
            await stopped.CancelAsync();
            await StopAsync(stopped.Token);
        }

        await _provider.DisposeAsync();
    }
}
