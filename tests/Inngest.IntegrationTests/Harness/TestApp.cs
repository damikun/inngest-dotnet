using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;

namespace Inngest.IntegrationTests.Harness;

// The app under test: the SDK's endpoint on a real Kestrel server, so the Inngest server in Docker can sync it and call
// its functions over HTTP exactly as it would in production.
public sealed class TestApp : IAsyncDisposable
{
    public const string ServePath = "/api/inngest";

    private readonly WebApplication _app;
    private bool _disposed;

    private TestApp(WebApplication app, int port)
    {
        _app = app;
        Port = port;
        Requests = app.Services.GetRequiredService<RequestLog>();
        Probe = app.Services.GetRequiredService<Probe>();
        Logs = app.Services.GetFakeLogCollector();
    }

    public int Port { get; }

    // Where the Inngest server reaches the app from inside its container. The SDK registers the URL it was synced
    // through, so this is also the base of every function's step URL.
    public Uri ServerFacingUrl => new($"http://host.docker.internal:{Port}{ServePath}");

    // Where tests call the app directly.
    public Uri LocalUrl => new($"http://localhost:{Port}{ServePath}");

    public IServiceProvider Services => _app.Services;

    public RequestLog Requests { get; }

    public Probe Probe { get; }

    public FakeLogCollector Logs { get; }

    public static async Task<TestApp> StartAsync(
        int port,
        Action<InngestOptions> configure,
        Action<IInngestBuilder> addFunctions,
        CancellationToken cancellationToken,
        Action<WebApplication>? configureApp = null)
    {
        var builder = WebApplication.CreateSlimBuilder();

        // Keeps INNGEST_* variables from the developer's environment out of the SDK's settings.
        builder.Configuration.Sources.Clear();

        // All interfaces, not loopback: on Linux the container connects through the Docker bridge.
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.ListenAnyIP(port));

        builder.Logging.ClearProviders();
        builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
        builder.Logging.AddFakeLogging();

        builder.Services.AddSingleton<Probe>();
        builder.Services.AddSingleton<RequestLog>();
        addFunctions(builder.Services.AddInngest(configure));

        var app = builder.Build();
        app.Use(app.Services.GetRequiredService<RequestLog>().InvokeAsync);
        configureApp?.Invoke(app);
        app.MapInngest(ServePath);

        try
        {
            await app.StartAsync(cancellationToken);
        }
        catch
        {
            await app.DisposeAsync();
            throw;
        }

        return new TestApp(app, port);
    }

    // A fixture that restarts the app stops it before the fixture itself is disposed.
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
