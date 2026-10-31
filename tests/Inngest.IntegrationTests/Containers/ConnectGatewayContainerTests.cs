using System.Net.Sockets;
using Inngest.IntegrationTests.Connect;
using Inngest.IntegrationTests.Harness;
using Inngest.Testing.Containers;
using Microsoft.Extensions.DependencyInjection;

namespace Inngest.IntegrationTests.Containers;

// A worker reaches the gateway at the port the server hands it, which is the one inside the container, so the container
// publishes that port on the host as well.
public sealed class ConnectGatewayContainerTests
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Theory(Timeout = 90_000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_worker_connects_through_the_gateway_and_runs_functions_without_an_endpoint(bool selfHosted)
    {
        var builder = new InngestServerBuilder(InngestServer.Image).WithConnectGateway();
        await using var server = (selfHosted ? builder.WithSelfHosting() : builder).Build();
        await server.StartAsync(Cancellation);

        // The worker is disposed first, so that it closes its connection while the gateway is still there.
        await using var worker = await ConnectWorkerApp.StartAsync(
            server.GetBaseAddress(),
            ContainerApp.AppId,
            inngest => ContainerApp.AddFunctions(inngest),
            Cancellation,
            configureOptions: server.Configure);
        using var api = new DevServerClient(server.GetBaseAddress(), server.EventKey ?? "test", server.SigningKey, token => DescribeAsync(server, token));

        // The worker takes work once it is active, as the guide says, and no wait for the server to list the app follows.
        var connection = worker.Services.GetRequiredService<IInngestConnection>();
        var state = connection.State;
        while (state != InngestConnectionState.Active)
        {
            state = await connection.WaitForStateChangedAsync(state, Cancellation);
        }

        var eventId = await api.SendEventAsync("containers/steps", new { value = 20 }, Cancellation);
        var run = await api.WaitForRunEndAsync(eventId, Cancellation);

        run.Status.ShouldBe(RunStatus.Completed);
        run.Output.ShouldNotBeNull().ShouldContainJson("""{ "result": 42 }""");
    }

    [Fact(Timeout = 90_000)]
    public async Task The_gateway_is_published_on_the_port_it_has_inside_the_container()
    {
        await using var server = new InngestServerBuilder(InngestServer.Image).WithConnectGateway().Build();
        await server.StartAsync(Cancellation);
        var port = server.ConnectGatewayPort.ShouldNotBeNull();

        using var client = new TcpClient();
        await client.ConnectAsync(server.Hostname, port, Cancellation);
        using var http = new HttpClient();
        using var ready = await http.GetAsync(new Uri($"http://{server.Hostname}:{port}/ready"), Cancellation);

        client.Connected.ShouldBeTrue();
        ready.EnsureSuccessStatusCode();
        server.GetMappedPublicPort(port).ShouldBe((ushort)port);
    }

    [Fact(Timeout = 90_000)]
    public async Task A_gateway_port_that_was_chosen_is_the_one_published()
    {
        var port = FreePorts.Pick(1)[0];
        await using var server = new InngestServerBuilder(InngestServer.Image).WithConnectGateway(port).Build();

        await server.StartAsync(Cancellation);

        server.ConnectGatewayPort.ShouldBe(port);
        server.GetMappedPublicPort(port).ShouldBe((ushort)port);
    }

    private static async Task<string> DescribeAsync(InngestServerContainer server, CancellationToken cancellationToken)
    {
        var (stdout, stderr) = await server.GetLogsAsync(timestampsEnabled: false, ct: cancellationToken);
        return string.Join('\n', (stdout + stderr).Split('\n', StringSplitOptions.RemoveEmptyEntries).TakeLast(30));
    }
}
