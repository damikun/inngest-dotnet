using Inngest.IntegrationTests.Harness;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;

namespace Inngest.IntegrationTests.Connect;

// An app that runs its functions as a Connect worker: the generic host with AddInngest and AddConnect, and no web
// server at all. The dev server reaches it only through the WebSocket the worker opens to the gateway.
public sealed class ConnectWorkerApp : IAsyncDisposable
{
    private readonly IHost _host;
    private bool _stopped;

    private ConnectWorkerApp(IHost host, string instanceId)
    {
        _host = host;
        InstanceId = instanceId;
        Probe = host.Services.GetRequiredService<Probe>();
        Logs = host.Services.GetFakeLogCollector();
    }

    // Unique per worker, so that the leases and capacity Inngest tracks per instance never mix between test runs.
    public string InstanceId { get; }

    public Probe Probe { get; }

    public FakeLogCollector Logs { get; }

    public IServiceProvider Services => _host.Services;

    public static async Task<ConnectWorkerApp> StartAsync(
        Uri serverUrl,
        string appId,
        Action<IInngestBuilder> addFunctions,
        CancellationToken cancellationToken,
        Action<IServiceCollection>? configureServices = null,
        Action<InngestOptions>? configureOptions = null)
    {
        var builder = Host.CreateApplicationBuilder();

        // Keeps INNGEST_* variables from the developer's environment out of the SDK's settings.
        builder.Configuration.Sources.Clear();
        builder.Logging.ClearProviders();
        builder.Logging.AddFakeLogging();

        var instanceId = $"itests-{Guid.NewGuid():N}";
        builder.Services.AddSingleton<Probe>();
        configureServices?.Invoke(builder.Services);
        var inngest = builder.Services.AddInngest(options =>
        {
            options.AppId = appId;
            options.IsDev = true;
            options.DevServerUrl = serverUrl;
            configureOptions?.Invoke(options);
        });
        addFunctions(inngest);
        inngest.AddConnect(options => options.InstanceId = instanceId);

        var host = builder.Build();
        try
        {
            await host.StartAsync(cancellationToken);
        }
        catch
        {
            host.Dispose();
            throw;
        }

        return new ConnectWorkerApp(host, instanceId);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _stopped = true;
        await _host.StopAsync(cancellationToken);
    }

    public IReadOnlyList<FakeLogRecord> Warnings() =>
        [.. Logs.GetSnapshot().Where(record => record.Level >= LogLevel.Warning)];

    public async ValueTask DisposeAsync()
    {
        if (!_stopped)
        {
            await _host.StopAsync(CancellationToken.None);
        }

        _host.Dispose();
    }
}
