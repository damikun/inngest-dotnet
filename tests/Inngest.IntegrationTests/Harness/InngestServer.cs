using System.Globalization;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;

namespace Inngest.IntegrationTests.Harness;

// The Inngest server in Docker, either the dev server or a self-hosted one that signs its requests. It is published on
// a fixed host port so that the app under test can be configured with its URL before either of them starts.
public sealed class InngestServer : IAsyncDisposable
{
    public const string Image = "inngest/inngest:v1.49.0";

    // Lets a developer find containers left behind by a killed test run.
    public const string Label = "inngest-dotnet.itests";

    private const ushort ApiPort = 8288;

    // Retry a failed sync every second, retry failed steps after one second instead of the 15 s+ backoff table, and
    // poll the queue every 50 ms so that consecutive steps run about 50 ms apart instead of 150 ms.
    private static readonly string[] FastTiming = ["--poll-interval", "1", "--retry-interval", "1", "--tick", "50"];

    private readonly IContainer _container;

    private InngestServer(IContainer container, int hostPort)
    {
        _container = container;
        Url = new Uri($"http://localhost:{hostPort}");
    }

    public Uri Url { get; }

    // The dev server neither signs requests nor checks event keys. Without --no-discovery it would scan ports inside
    // the container; with --no-poll it syncs the app once instead of every few seconds.
    public static InngestServer Dev(int hostPort, Uri appUrl) =>
        Create(hostPort, ["inngest", "dev", "--no-discovery", "--no-poll", .. FastTiming, "-u", appUrl.AbsoluteUri]);

    // The dev server for Connect workers, which sync through its gateway instead of being called at a URL. The start
    // API hands workers ws://<host>:<gateway port>/v0/connect with the port inside the container, so the gateway port
    // is published on the same port of the host.
    public static InngestServer DevWithConnect(int hostPort, int gatewayPort) =>
        Create(
            hostPort,
            ["inngest", "dev", "--no-discovery", "--no-poll", .. FastTiming, "--connect-gateway-port", gatewayPort.ToString(CultureInfo.InvariantCulture)],
            gatewayPort);

    // A self-hosted server signs every request to the app and wants the signing key on its REST API. The key must be
    // plain hex: with a signkey-<env>- prefix it either refuses to start or signs syncs and calls with different keys.
    public static InngestServer SelfHosted(int hostPort, Uri appUrl, string signingKey, string eventKey) =>
        Create(
            hostPort,
            ["inngest", "start", "--signing-key", signingKey, "--event-key", eventKey, .. FastTiming, "-u", appUrl.AbsoluteUri]);

    // A self-hosted server with its Connect gateway, for workers in cloud mode. They sync through the gateway, so there
    // is no app URL to call.
    public static InngestServer SelfHostedWithConnect(int hostPort, int gatewayPort, string signingKey, string eventKey) =>
        Create(
            hostPort,
            [
                "inngest", "start", "--signing-key", signingKey, "--event-key", eventKey, .. FastTiming,
                "--connect-gateway-port", gatewayPort.ToString(CultureInfo.InvariantCulture),
            ],
            gatewayPort);

    private static InngestServer Create(int hostPort, string[] command, int? gatewayPort = null)
    {
        var readiness = Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(
            request => request.ForPort(ApiPort).ForPath("/health"),
            // The server is up about 0.25 s after start; polling once a second would add most of a second.
            wait => wait.WithInterval(TimeSpan.FromMilliseconds(100)).WithTimeout(TimeSpan.FromSeconds(60)));

        var builder = new ContainerBuilder(Image)
            .WithName($"inngest-itests-{Guid.NewGuid():N}")
            .WithLabel(Label, "true")
            .WithCommand(command)
            .WithPortBinding(hostPort, ApiPort)
            // Docker Desktop resolves host.docker.internal on its own; Linux needs it mapped to the host gateway.
            .WithExtraHost("host.docker.internal", "host-gateway")
            .WithEnvironment("DO_NOT_TRACK", "1");

        if (gatewayPort is { } port)
        {
            builder = builder.WithPortBinding(port, port);
            readiness = readiness.UntilHttpRequestIsSucceeded(
                request => request.ForPort((ushort)port).ForPath("/ready"),
                wait => wait.WithInterval(TimeSpan.FromMilliseconds(100)).WithTimeout(TimeSpan.FromSeconds(60)));
        }

        return new InngestServer(builder.WithWaitStrategy(readiness).Build(), hostPort);
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _container.StartAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            var logs = await GetLogsAsync(CancellationToken.None);
            throw new InvalidOperationException($"The Inngest server ({Image}) did not become healthy. Its log:\n{logs}", exception);
        }
    }

    // The server logs JSON lines; the interesting ones for a failing test are WARN and ERROR, such as
    // "error handling queue item" with the reason a call to the app failed.
    public async Task<string> GetLogsAsync(CancellationToken cancellationToken)
    {
        try
        {
            var (stdout, stderr) = await _container.GetLogsAsync(timestampsEnabled: false, ct: cancellationToken);
            return stdout + stderr;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return $"(the container log is not available: {exception.Message})";
        }
    }

    public ValueTask DisposeAsync() => _container.DisposeAsync();
}
