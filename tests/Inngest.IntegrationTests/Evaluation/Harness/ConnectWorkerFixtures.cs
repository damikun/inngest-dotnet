using System.Security.Cryptography;
using System.Text;
using Inngest.Functions;
using Inngest.IntegrationTests.Connect;
using Inngest.IntegrationTests.Evaluation.Functions;
using Inngest.IntegrationTests.Harness;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Inngest.IntegrationTests.Evaluation.Harness;

// An Inngest server with its Connect gateway and a worker app that runs a set of functions over the WebSocket, set up
// the way ConnectFixture sets up its own. Scenarios can start further workers against the same server.
public abstract class ConnectWorkerFixture : IEvaluationFixture
{
    // The server's own gRPC ports inside the container, which the gateway port must not take.
    private static readonly HashSet<int> ContainerPorts = [50052, 50053];

    private readonly CancellationTokenSource _disposing = new();
    private readonly Lock _syncLock = new();
    private readonly List<ConnectWorkerApp> _workers = [];
    private Task? _sync;

    public abstract string AppId { get; }

    public Uri ServerUrl { get; private set; } = null!;

    public InngestServer Server { get; private set; } = null!;

    public DevServerClient Api { get; private set; } = null!;

    public ConnectWorkerApp Worker { get; private set; } = null!;

    public Probe Probe => Worker.Probe;

    protected virtual string EventKey => "eval";

    protected virtual string? ApiKey => null;

    public async ValueTask InitializeAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var ports = FreePorts.Pick(4).Where(port => !ContainerPorts.Contains(port)).Take(2).ToArray();
        ServerUrl = new Uri($"http://localhost:{ports[0]}");
        try
        {
            Server = CreateServer(ports[0], ports[1]);
            Api = new DevServerClient(ServerUrl, EventKey, ApiKey, DescribeAsync);
            await Server.StartAsync(cancellationToken);
            Worker = await StartWorkerAsync(AppId, AddFunctions, cancellationToken);
        }
        catch
        {
            await DisposeAsync();
            throw;
        }
    }

    // Starts a worker against the same server, for this fixture or for a scenario with an app of its own. The fixture
    // disposes every worker at the end, so scenarios only ever stop theirs.
    public async Task<ConnectWorkerApp> StartWorkerAsync(
        string appId,
        Action<IInngestBuilder> addFunctions,
        CancellationToken cancellationToken,
        Action<IServiceCollection>? configureServices = null)
    {
        var worker = await ConnectWorkerApp.StartAsync(
            ServerUrl,
            appId,
            addFunctions,
            cancellationToken,
            services =>
            {
                services.AddSingleton<Gates>();
                configureServices?.Invoke(services);
            },
            ConfigureOptions);

        lock (_workers)
        {
            _workers.Add(worker);
        }

        return worker;
    }

    public Task EnsureSyncedAsync(CancellationToken cancellationToken)
    {
        lock (_syncLock)
        {
            _sync ??= WaitForSyncAsync(AppId, Worker, _disposing.Token);
        }

        return _sync.WaitAsync(cancellationToken);
    }

    // Waits until the server lists every function the worker registered.
    public Task WaitForSyncAsync(string appId, ConnectWorkerApp worker, CancellationToken cancellationToken) =>
        Api.WaitForFunctionsAsync(
            appId,
            [.. worker.Services.GetServices<FunctionDefinition>().Select(function => $"{appId}-{function.Id}")],
            cancellationToken,
            InngestFixture.SyncTimeout);

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

        List<ConnectWorkerApp> workers;
        lock (_workers)
        {
            workers = [.. _workers];
        }

        foreach (var worker in workers)
        {
            text.AppendLine($"--- Information and above logged by worker {worker.InstanceId} ---");
            foreach (var record in worker.Logs.GetSnapshot().Where(record => record.Level >= LogLevel.Information).TakeLast(20))
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

        // Workers go first, so that they close their connections while the gateway is still there.
        List<ConnectWorkerApp> workers;
        lock (_workers)
        {
            workers = [.. _workers];
        }

        foreach (var worker in workers)
        {
            await worker.DisposeAsync();
        }

        if (Server is not null)
        {
            await Server.DisposeAsync();
        }

        Api?.Dispose();
        _disposing.Dispose();
        GC.SuppressFinalize(this);
    }

    protected abstract InngestServer CreateServer(int hostPort, int gatewayPort);

    protected abstract void AddFunctions(IInngestBuilder inngest);

    // Applied after the worker's dev mode defaults.
    protected virtual void ConfigureOptions(InngestOptions options)
    {
    }
}

// The dev server's gateway and a worker in dev mode.
public sealed class EvaluationConnectFixture : ConnectWorkerFixture
{
    public override string AppId => "eval-connect";

    protected override InngestServer CreateServer(int hostPort, int gatewayPort) => InngestServer.DevWithConnect(hostPort, gatewayPort);

    protected override void AddFunctions(IInngestBuilder inngest)
    {
        inngest.RecordOutgoingRequests();
        ConnectWorkerFunctions.Add(inngest);
    }
}

// A self-hosted server's gateway and a worker in cloud mode, in the middle of a signing key rotation: the server knows
// only the old key, while the worker has the new key and the old one as its fallback.
public sealed class CloudConnectFixture : ConnectWorkerFixture
{
    public string OldKey { get; } = CloudFixture.NewKey();

    public string NewKey { get; } = CloudFixture.NewKey();

    public string ServerEventKey { get; } = $"eval-{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8))}";

    public override string AppId => "eval-connect-cloud";

    protected override string EventKey => ServerEventKey;

    protected override string? ApiKey => OldKey;

    protected override InngestServer CreateServer(int hostPort, int gatewayPort) =>
        InngestServer.SelfHostedWithConnect(hostPort, gatewayPort, OldKey, ServerEventKey);

    protected override void AddFunctions(IInngestBuilder inngest)
    {
        inngest.RecordOutgoingRequests();
        ConnectWorkerFunctions.AddCloud(inngest);
    }

    protected override void ConfigureOptions(InngestOptions options)
    {
        options.IsDev = false;
        options.BaseUrl = ServerUrl;
        options.SigningKey = NewKey;
        options.SigningKeyFallback = OldKey;
        options.EventKey = ServerEventKey;
    }
}
