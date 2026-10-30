using Inngest.IntegrationTests.Harness;
using Inngest.Testing.Containers;

namespace Inngest.IntegrationTests.Containers;

// The other way to introduce the app to the server: the container is told the app's address when it is built, and syncs
// the app itself, so the app may start after the server. That needs the app's port before either starts.
public sealed class AppUrlContainerTests
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Theory(Timeout = 90_000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task The_server_keeps_trying_until_an_app_that_starts_after_it_answers_and_then_runs_its_functions(bool selfHosted)
    {
        var port = FreePorts.Pick(1)[0];
        var builder = new InngestBuilder(InngestServer.Image).WithAppUrl($"http://localhost:{port}{ContainerApp.ServePath}");
        await using var server = (selfHosted ? builder.WithSelfHosting() : builder).Build();
        await server.StartAsync(Cancellation);

        // The server has tried to sync an address that nobody listens on, and tries again every second.
        await Task.Delay(TimeSpan.FromSeconds(3), Cancellation);
        await using var app = await ContainerApp.StartAsync(server.Configure, settings: null, Cancellation, port);
        using var api = new DevServerClient(server.GetBaseAddress(), server.EventKey ?? "test", server.SigningKey, _ => Task.FromResult(app.Describe()));

        await api.WaitForFunctionsAsync(ContainerApp.AppId, ContainerApp.Slugs, Cancellation, TimeSpan.FromSeconds(20));
        var eventId = await api.SendEventAsync("containers/steps", new { value = 5 }, Cancellation);
        var run = await api.WaitForRunEndAsync(eventId, Cancellation);

        run.Status.ShouldBe(RunStatus.Completed);
        run.Output.ShouldNotBeNull().ShouldContainJson("""{ "result": 12 }""");

        // The server asked for the sync at the address the container reaches the host by, once the app answered, and
        // with a signature when it is the self-hosted server.
        var syncs = app.Requests.Snapshot().Where(request => request.Method == "PUT").ToList();
        syncs.ShouldNotBeEmpty();
        syncs.ShouldAllBe(request => request.Signed == selfHosted);
        syncs[0].BodyText.ShouldContain($"http://host.docker.internal:{port}{ContainerApp.ServePath}");
    }

    [Fact(Timeout = 90_000)]
    public async Task One_server_syncs_several_apps()
    {
        var ports = FreePorts.Pick(2);
        var builder = new InngestBuilder(InngestServer.Image)
            .WithAppUrl(new Uri($"http://localhost:{ports[0]}{ContainerApp.ServePath}"))
            .WithAppUrl(new Uri($"http://127.0.0.1:{ports[1]}{ContainerApp.ServePath}"));
        await using var server = builder.Build();
        await server.StartAsync(Cancellation);

        // Two apps of one id would be one app to the server; the second gets an id of its own.
        await using var first = await ContainerApp.StartAsync(server.Configure, settings: null, Cancellation, ports[0]);
        await using var second = await ContainerApp.StartAsync(options => { server.Configure(options); options.AppId = "containers-two"; }, settings: null, Cancellation, ports[1]);
        using var api = new DevServerClient(server.GetBaseAddress(), "test", apiKey: null, _ => Task.FromResult(first.Describe() + second.Describe()));

        await api.WaitForFunctionsAsync(ContainerApp.AppId, ContainerApp.Slugs, Cancellation, TimeSpan.FromSeconds(20));
        await api.WaitForFunctionsAsync("containers-two", ["containers-two-hello", "containers-two-steps"], Cancellation, TimeSpan.FromSeconds(20));
    }
}
