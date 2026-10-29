using System.Text;
using System.Text.Json;
using Inngest.IntegrationTests.Harness;
using Microsoft.Extensions.Logging;

namespace Inngest.IntegrationTests.Connect;

// A dev server whose connect gateway is published on a fixed port, and an app that runs as a Connect worker against
// it. The server starts first: the worker connects on its own, and the gateway syncs the app from the worker's
// handshake, so the app has no URL for the server to call.
public sealed class ConnectFixture : IAsyncLifetime
{
    private const string EventKey = "itests";

    // The dev server's own gRPC ports inside the container, which the gateway port must not take.
    private static readonly HashSet<int> ContainerPorts = [50052, 50053];

    private readonly CancellationTokenSource _disposing = new();
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private readonly Lock _syncLock = new();
    private Task? _sync;

    public Uri ServerUrl { get; private set; } = null!;

    public InngestServer Server { get; private set; } = null!;

    public DevServerClient Api { get; private set; } = null!;

    public ConnectWorkerApp Worker { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var ports = FreePorts.Pick(4).Where(port => !ContainerPorts.Contains(port)).Take(2).ToArray();
        ServerUrl = new Uri($"http://localhost:{ports[0]}");
        try
        {
            Server = InngestServer.DevWithConnect(ports[0], ports[1]);
            Api = new DevServerClient(ServerUrl, EventKey, apiKey: null, DescribeAsync);
            await Server.StartAsync(cancellationToken);
            Worker = await ConnectWorkerApp.StartAsync(ServerUrl, ConnectFunctions.AppId, inngest => inngest.AddConnectFunctions(), cancellationToken);
        }
        catch
        {
            await DisposeAsync();
            throw;
        }
    }

    // The first caller starts the wait and everyone shares its outcome, as in InngestFixture.
    public Task EnsureSyncedAsync(CancellationToken cancellationToken)
    {
        lock (_syncLock)
        {
            _sync ??= Api.WaitForFunctionsAsync(ConnectFunctions.AppId, ConnectFunctions.Slugs, _disposing.Token, InngestFixture.SyncTimeout);
        }

        return _sync.WaitAsync(cancellationToken);
    }

    // The connections the server has recorded for a worker instance, latest first, from its connection history.
    public async Task<IReadOnlyList<WorkerConnection>> GetConnectionsAsync(string instanceId, CancellationToken cancellationToken)
    {
        const string Query = """
            query {
              workerConnections(first: 100, orderBy: [{field: CONNECTED_AT, direction: DESC}], filter: {from: "2000-01-01T00:00:00Z"}) {
                edges { node { id instanceId appName status disconnectReason sdkLang sdkVersion os } }
              }
            }
            """;

        using var body = await Api.QueryAsync(Query, variables: null, cancellationToken);
        return
        [
            .. body.RootElement.GetProperty("data").GetProperty("workerConnections").GetProperty("edges").EnumerateArray()
                .Select(edge => WorkerConnection.FromJson(edge.GetProperty("node")))
                .Where(connection => connection.InstanceId == instanceId),
        ];
    }

    // The instance ids of the connections the gateway holds right now. The dev server ignores the environment in the
    // path of this debug endpoint.
    public async Task<IReadOnlyList<string>> GetLiveInstancesAsync(CancellationToken cancellationToken)
    {
        var url = new Uri(ServerUrl, $"/v0/connect/envs/dev/conns?cb={Guid.NewGuid():N}");
        using var body = JsonDocument.Parse(await _http.GetStringAsync(url, cancellationToken));
        return [.. body.RootElement.GetProperty("data").EnumerateArray().Select(connection => connection.GetProperty("instance_id").GetString()!)];
    }

    // Sync state, the worker's warnings and errors, and the server's log.
    public async Task<string> DescribeAsync(CancellationToken cancellationToken)
    {
        var text = new StringBuilder();
        text.AppendLine("--- Apps on the Inngest server ---");
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

        if (Worker is not null)
        {
            text.AppendLine("--- Information and above logged by the worker ---");
            foreach (var record in Worker.Logs.GetSnapshot().Where(record => record.Level >= LogLevel.Information).TakeLast(30))
            {
                text.AppendLine($"{record.Timestamp:HH:mm:ss.fff} {record.Level} {record.Category}: {record.Message}");
                if (record.Exception is { } exception)
                {
                    text.AppendLine(string.Join('\n', exception.ToString().Split('\n').Take(8)));
                }
            }
        }

        text.AppendLine("--- Inngest server log tail ---");
        foreach (var line in (await Server.GetLogsAsync(cancellationToken)).Split('\n', StringSplitOptions.RemoveEmptyEntries).TakeLast(30))
        {
            text.AppendLine(line);
        }

        return text.ToString();
    }

    public async ValueTask DisposeAsync()
    {
        await _disposing.CancelAsync();

        // The worker goes first, so that it closes its connection while the gateway is still there.
        if (Worker is not null)
        {
            await Worker.DisposeAsync();
        }

        if (Server is not null)
        {
            await Server.DisposeAsync();
        }

        Api?.Dispose();
        _http.Dispose();
        _disposing.Dispose();
    }
}

// The Connect tests share one server and run one class after the other.
[CollectionDefinition(Name)]
public sealed class ConnectCollection : ICollectionFixture<ConnectFixture>
{
    public const string Name = "Connect";
}

public sealed record WorkerConnection(
    string Id,
    string InstanceId,
    string? AppName,
    string? Status,
    string? DisconnectReason,
    string? SdkLanguage,
    string? SdkVersion,
    string? Os)
{
    public static WorkerConnection FromJson(JsonElement node) => new(
        node.GetProperty("id").GetString()!,
        node.GetProperty("instanceId").GetString()!,
        Text(node, "appName"),
        Text(node, "status"),
        Text(node, "disconnectReason"),
        Text(node, "sdkLang"),
        Text(node, "sdkVersion"),
        Text(node, "os"));

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
