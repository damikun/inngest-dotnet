using System.Text;
using Inngest.IntegrationTests.Harness;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;

namespace Inngest.IntegrationTests.Containers;

// A small app with the SDK's endpoint on a real Kestrel server, on a port the system picks, so that nothing has to be
// decided before the container starts. It listens on all interfaces: on Linux the container reaches it through the
// Docker bridge, not through loopback.
public sealed class ContainerApp : IAsyncDisposable
{
    public const string AppId = "containers";
    public const string ServePath = "/api/inngest";

    // What the server must list once the app is synced: "<app id>-<function id>".
    public static IReadOnlyCollection<string> Slugs { get; } = [$"{AppId}-hello", $"{AppId}-steps"];

    private readonly WebApplication _app;
    private bool _disposed;

    private ContainerApp(WebApplication app, int port)
    {
        _app = app;
        Port = port;
        Requests = app.Services.GetRequiredService<RequestLog>();
        Logs = app.Services.GetFakeLogCollector();
    }

    public int Port { get; }

    // Where the test reaches the app. The container reaches it as host.docker.internal on the same port.
    public Uri Url => new($"http://127.0.0.1:{Port}{ServePath}");

    public IServiceProvider Services => _app.Services;

    public RequestLog Requests { get; }

    public FakeLogCollector Logs { get; }

    // hello answers at once; steps runs two steps, the second with the first one's result, and so goes through the server
    // for every step.
    public static IInngestBuilder AddFunctions(IInngestBuilder inngest)
    {
        inngest.AddFunction(
            new FunctionOptions { Id = "hello", Triggers = [Trigger.Event("containers/hello")] },
            (context, _) => Task.FromResult<object?>(new { greeting = $"Hello, {context.Event.Data.GetProperty("name").GetString()}!" }));

        inngest.AddFunction(
            new FunctionOptions { Id = "steps", Triggers = [Trigger.Event("containers/steps")] },
            async (context, _) =>
            {
                var value = context.Event.Data.GetProperty("value").GetInt32();
                var doubled = await context.Step.RunAsync("double", () => value * 2);
                var total = await context.Step.RunAsync("add", () => doubled + 2);
                return new { result = total };
            });

        return inngest;
    }

    // The settings are the options the container configures, or the variables it lists, which stand in for the
    // environment of the process. Port 0 lets the system pick the port.
    public static async Task<ContainerApp> StartAsync(
        Action<InngestOptions>? configure,
        IReadOnlyDictionary<string, string>? settings,
        CancellationToken cancellationToken,
        int port = 0)
    {
        var builder = WebApplication.CreateSlimBuilder();

        // Keeps INNGEST_* variables from the developer's environment out of the SDK's settings.
        builder.Configuration.Sources.Clear();
        if (settings is not null)
        {
            builder.Configuration.AddInMemoryCollection(settings.Select(setting => new KeyValuePair<string, string?>(setting.Key, setting.Value)));
        }

        builder.WebHost.ConfigureKestrel(kestrel => kestrel.ListenAnyIP(port));
        builder.Logging.ClearProviders();
        builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
        builder.Logging.AddFakeLogging();

        builder.Services.AddSingleton<RequestLog>();
        AddFunctions(builder.Services.AddInngest(options =>
        {
            options.AppId = AppId;
            configure?.Invoke(options);
        }));

        var app = builder.Build();
        app.Use(app.Services.GetRequiredService<RequestLog>().InvokeAsync);
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

        // Kestrel lists the endpoint it bound, with the port it was given.
        return new ContainerApp(app, new Uri(app.Urls.First()).Port);
    }

    // Sync state, what reached the app and the app's warnings and errors: what a failing test needs to see next to the
    // server's own log.
    public string Describe()
    {
        var text = new StringBuilder();
        text.AppendLine("--- Requests that reached the app, latest last ---");
        foreach (var request in Requests.Snapshot().TakeLast(30))
        {
            text.AppendLine(request.ToString());
        }

        text.AppendLine("--- Warnings and errors logged by the app ---");
        foreach (var record in Logs.GetSnapshot().Where(record => record.Level >= LogLevel.Warning).TakeLast(15))
        {
            text.AppendLine($"{record.Timestamp:HH:mm:ss.fff} {record.Level} {record.Category}: {record.Message}");
        }

        return text.ToString();
    }

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
