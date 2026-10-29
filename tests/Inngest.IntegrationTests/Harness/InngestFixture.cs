using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;

namespace Inngest.IntegrationTests.Harness;

// Runs the app under test and an Inngest server that syncs it. Both ports are picked up front and the app starts
// first, so the server's first sync attempt already finds it.
//
// Waiting for the sync is separate from starting (EnsureSyncedAsync), so that tests of the harness itself can run
// even when the SDK cannot be synced.
public abstract class InngestFixture : IAsyncLifetime
{
    // Well under the 30 s test timeout, so a broken sync fails with its explanation rather than a bare timeout.
    public static readonly TimeSpan SyncTimeout = TimeSpan.FromSeconds(15);

    private readonly CancellationTokenSource _disposing = new();
    private readonly Lock _syncLock = new();
    private Task? _sync;
    private bool _disposed;

    // Replaced only by fixtures that restart the app.
    public TestApp App { get; protected set; } = null!;

    public InngestServer Server { get; private set; } = null!;

    public DevServerClient Api { get; private set; } = null!;

    public abstract string AppId { get; }

    // The ids, with the app prefix, that the server must list once the app is synced.
    public abstract IReadOnlyCollection<string> ExpectedFunctions { get; }

    protected abstract string EventKey { get; }

    // Sent as a bearer token to the server's REST API. The dev server needs none.
    protected virtual string? ApiKey => null;

    protected abstract InngestServer CreateServer(int hostPort, Uri appUrl);

    protected abstract void ConfigureInngest(InngestOptions options, Uri serverUrl);

    protected abstract void AddFunctions(IInngestBuilder inngest);

    public async ValueTask InitializeAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var ports = FreePorts.Pick(2);
        var serverUrl = new Uri($"http://localhost:{ports[1]}");
        try
        {
            App = await TestApp.StartAsync(ports[0], options => ConfigureInngest(options, serverUrl), AddFunctions, cancellationToken);
            Server = CreateServer(ports[1], App.ServerFacingUrl);
            Api = new DevServerClient(serverUrl, EventKey, ApiKey, DescribeAsync);
            await Server.StartAsync(cancellationToken);
        }
        catch
        {
            await DisposeAsync();
            throw;
        }
    }

    // The first caller starts the wait and everyone shares its outcome, so a broken sync fails every test that needs
    // it quickly and with the same explanation.
    public Task EnsureSyncedAsync(CancellationToken cancellationToken)
    {
        lock (_syncLock)
        {
            _sync ??= Api.WaitForFunctionsAsync(AppId, ExpectedFunctions, _disposing.Token, SyncTimeout);
        }

        return _sync.WaitAsync(cancellationToken);
    }

    // Everything that helps explain a failing test: sync state, what reached the app, the app's warnings and errors,
    // and the server's log.
    public async Task<string> DescribeAsync(CancellationToken cancellationToken)
    {
        var text = new StringBuilder();

        text.AppendLine("--- Apps on the Inngest server (sync errors show here, the server does not log them) ---");
        try
        {
            foreach (var app in await Api.GetAppsAsync(cancellationToken))
            {
                text.AppendLine(app.ToString());
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            text.AppendLine($"(not available: {exception.Message})");
        }

        text.AppendLine("--- Requests that reached the app, latest last ---");
        foreach (var request in App.Requests.Snapshot().TakeLast(30))
        {
            text.AppendLine(request.ToString());
        }

        text.AppendLine("--- Warnings and errors logged by the app, repeats collapsed ---");
        foreach (var (record, repeats) in Collapse(App.Logs.GetSnapshot().Where(record => record.Level >= LogLevel.Warning)).TakeLast(15))
        {
            text.AppendLine(Format(record));
            if (repeats > 0)
            {
                text.AppendLine($"    (and {repeats} more like it)");
            }
        }

        var log = (await Server.GetLogsAsync(cancellationToken)).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        text.AppendLine("--- Inngest server warnings and errors ---");
        foreach (var line in log.Where(line => line.Contains("\"level\":\"WARN\"", StringComparison.Ordinal)
            || line.Contains("\"level\":\"ERROR\"", StringComparison.Ordinal)).TakeLast(30))
        {
            text.AppendLine(line);
        }

        text.AppendLine("--- Inngest server log tail ---");
        foreach (var line in log.TakeLast(15))
        {
            text.AppendLine(line);
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
        await _disposing.CancelAsync();

        // The server goes first, so it stops calling an app that is shutting down.
        if (Server is not null)
        {
            await Server.DisposeAsync();
        }

        if (App is not null)
        {
            await App.DisposeAsync();
        }

        Api?.Dispose();
        _disposing.Dispose();
        GC.SuppressFinalize(this);
    }

    // Kestrel logs the same unhandled exception for every failing request, each with its own connection id.
    private static List<(FakeLogRecord Record, int Repeats)> Collapse(IEnumerable<FakeLogRecord> records)
    {
        var collapsed = new List<(FakeLogRecord Record, int Repeats)>();
        foreach (var record in records)
        {
            if (collapsed.Count > 0 && Key(collapsed[^1].Record) == Key(record))
            {
                collapsed[^1] = (collapsed[^1].Record, collapsed[^1].Repeats + 1);
            }
            else
            {
                collapsed.Add((record, 0));
            }
        }

        return collapsed;

        static string Key(FakeLogRecord record) =>
            $"{record.Level}|{record.Category}|{record.Exception?.GetType().FullName}|{record.Exception?.Message ?? record.Message}";
    }

    private static string Format(FakeLogRecord record)
    {
        var line = $"{record.Timestamp:HH:mm:ss.fff} {record.Level} {record.Category}: {record.Message}";
        return record.Exception is null
            ? line
            : $"{line}\n{string.Join('\n', record.Exception.ToString().Split('\n').Take(8))}";
    }
}
