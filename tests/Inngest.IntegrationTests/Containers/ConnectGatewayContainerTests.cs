using System.Net.Sockets;
using Inngest.IntegrationTests.Connect;
using Inngest.IntegrationTests.Harness;
using Inngest.Testing.Containers;

namespace Inngest.IntegrationTests.Containers;

// A worker reaches the gateway at the port the server hands it, which is the one inside the container, so the container
// publishes that port on the host as well.
public sealed class ConnectGatewayContainerTests : IAsyncLifetime
{
    private InngestContainer _server = null!;
    private ConnectWorkerApp _worker = null!;
    private DevServerClient _api = null!;

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _server = new InngestBuilder(InngestServer.Image).WithConnectGateway().Build();
        try
        {
            await _server.StartAsync(Cancellation);
            _worker = await ConnectWorkerApp.StartAsync(
                _server.GetBaseAddress(),
                ContainerApp.AppId,
                inngest => ContainerApp.AddFunctions(inngest),
                Cancellation,
                configureOptions: _server.Configure);
            _api = new DevServerClient(_server.GetBaseAddress(), "test", apiKey: null, DescribeAsync);
        }
        catch
        {
            await DisposeAsync();
            throw;
        }
    }

    [Fact(Timeout = 60_000)]
    public async Task A_worker_connects_through_the_gateway_and_runs_functions_without_an_endpoint()
    {
        await _api.WaitForFunctionsAsync(ContainerApp.AppId, ContainerApp.Slugs, Cancellation, TimeSpan.FromSeconds(30));

        var eventId = await _api.SendEventAsync("containers/hello", new { name = "Ada" }, Cancellation);
        var run = await _api.WaitForRunEndAsync(eventId, Cancellation);

        run.Status.ShouldBe(RunStatus.Completed);
        run.Output.ShouldNotBeNull().ShouldContainJson("""{ "greeting": "Hello, Ada!" }""");
    }

    [Fact(Timeout = 60_000)]
    public async Task The_gateway_is_published_on_the_port_it_has_inside_the_container()
    {
        var port = _server.ConnectGatewayPort.ShouldNotBeNull();

        using var client = new TcpClient();
        await client.ConnectAsync(_server.Hostname, port, Cancellation);
        using var http = new HttpClient();
        using var ready = await http.GetAsync(new Uri($"http://{_server.Hostname}:{port}/ready"), Cancellation);

        client.Connected.ShouldBeTrue();
        ready.EnsureSuccessStatusCode();
        _server.GetMappedPublicPort(port).ShouldBe((ushort)port);
    }

    private async Task<string> DescribeAsync(CancellationToken cancellationToken)
    {
        var (stdout, stderr) = await _server.GetLogsAsync(timestampsEnabled: false, ct: cancellationToken);
        return string.Join('\n', (stdout + stderr).Split('\n', StringSplitOptions.RemoveEmptyEntries).TakeLast(30));
    }

    public async ValueTask DisposeAsync()
    {
        // The worker goes first, so that it closes its connection while the gateway is still there.
        if (_worker is not null)
        {
            await _worker.DisposeAsync();
        }

        if (_server is not null)
        {
            await _server.DisposeAsync();
        }

        _api?.Dispose();
        GC.SuppressFinalize(this);
    }
}
