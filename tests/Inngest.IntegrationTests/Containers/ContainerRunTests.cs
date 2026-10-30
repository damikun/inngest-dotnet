using Inngest.IntegrationTests.Harness;
using Microsoft.Extensions.DependencyInjection;

namespace Inngest.IntegrationTests.Containers;

// What every container gives a test, whichever server it runs and however the app is told about it: events reach the
// app's functions through the server, and the app's own client reaches the server.
public abstract class ContainerRunTests<TFixture>(TFixture fixture)
    where TFixture : ContainerFixture
{
    protected TFixture Fixture { get; } = fixture;

    protected static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact(Timeout = 60_000)]
    public async Task A_synced_app_runs_a_function_when_an_event_arrives()
    {
        var eventId = await Fixture.Api.SendEventAsync("containers/hello", new { name = "Ada" }, Cancellation);

        var run = await Fixture.Api.WaitForRunEndAsync(eventId, Cancellation);

        run.Status.ShouldBe(RunStatus.Completed);
        run.Output.ShouldNotBeNull().ShouldContainJson("""{ "greeting": "Hello, Ada!" }""");
    }

    [Fact(Timeout = 60_000)]
    public async Task A_function_with_steps_is_called_by_the_server_once_for_each_step()
    {
        var eventId = await Fixture.Api.SendEventAsync("containers/steps", new { value = 20 }, Cancellation);

        var run = await Fixture.Api.WaitForRunEndAsync(eventId, Cancellation);

        run.Status.ShouldBe(RunStatus.Completed);
        run.Output.ShouldNotBeNull().ShouldContainJson("""{ "result": 42 }""");
        Fixture.App.Requests.Snapshot().Count(request => request.Method == "POST").ShouldBeGreaterThanOrEqualTo(3);
    }

    [Fact(Timeout = 60_000)]
    public async Task The_app_s_own_client_sends_events_to_the_container()
    {
        var client = Fixture.App.Services.GetRequiredService<IInngestClient>();

        var sent = await client.SendAsync(new InngestEvent("containers/hello", new { name = "Grace" }), Cancellation);
        var run = await Fixture.Api.WaitForRunEndAsync(sent.Ids.Single(), Cancellation);

        run.Status.ShouldBe(RunStatus.Completed);
        run.Output.ShouldNotBeNull().ShouldContainJson("""{ "greeting": "Hello, Grace!" }""");
    }

    [Fact(Timeout = 60_000)]
    public async Task Syncing_an_app_again_changes_nothing()
    {
        await Should.NotThrowAsync(() => Fixture.Server.SyncAsync(Fixture.App.Url, Cancellation));

        var functions = await Fixture.Api.GetAppFunctionsAsync(ContainerApp.AppId, Cancellation);
        functions.Select(function => function.Slug).ShouldBe(ContainerApp.Slugs, ignoreOrder: true);
    }

    [Fact(Timeout = 60_000)]
    public async Task An_app_can_be_named_by_the_address_that_kestrel_reports_for_all_interfaces()
    {
        var kestrelName = new Uri($"http://[::]:{Fixture.App.Port}{ContainerApp.ServePath}");

        await Should.NotThrowAsync(() => Fixture.Server.SyncAsync(kestrelName, Cancellation));
    }

    [Fact(Timeout = 60_000)]
    public async Task Syncing_an_app_that_is_not_there_says_so()
    {
        var closed = new Uri($"http://127.0.0.1:{FreePorts.Pick(1)[0]}{ContainerApp.ServePath}");

        var exception = await Should.ThrowAsync<HttpRequestException>(() => Fixture.Server.SyncAsync(closed, Cancellation));

        exception.Message.ShouldNotBeEmpty();
    }
}

public sealed class DevServerContainerTests(DevContainerFixture fixture)
    : ContainerRunTests<DevContainerFixture>(fixture), IClassFixture<DevContainerFixture>
{
    [Fact(Timeout = 60_000)]
    public void The_container_is_the_dev_server_and_has_no_keys()
    {
        Fixture.Server.IsDevServer.ShouldBeTrue();
        Fixture.Server.SigningKey.ShouldBeNull();
        Fixture.Server.EventKey.ShouldBeNull();
    }

    [Fact(Timeout = 60_000)]
    public async Task The_server_says_it_is_a_dev_server()
    {
        using var http = new HttpClient();
        using var response = await http.GetAsync(new Uri(Fixture.Server.GetBaseAddress(), "health"), Cancellation);

        response.EnsureSuccessStatusCode();
        response.Headers.GetValues("X-Inngest-Server-Kind").ShouldBe(["dev"]);
    }

    [Fact(Timeout = 60_000)]
    public async Task The_dev_server_takes_any_event_key()
    {
        using var other = new DevServerClient(Fixture.Server.GetBaseAddress(), "any-key-at-all", apiKey: null, Fixture.DescribeAsync);

        var eventId = await other.SendEventAsync("containers/hello", new { name = "Linus" }, Cancellation);
        var run = await Fixture.Api.WaitForRunEndAsync(eventId, Cancellation);

        run.Status.ShouldBe(RunStatus.Completed);
    }

    [Fact(Timeout = 60_000)]
    public void The_dev_server_neither_signs_its_requests_nor_is_asked_to()
    {
        Fixture.App.Requests.Snapshot().ShouldNotBeEmpty();
        Fixture.App.Requests.Snapshot().ShouldAllBe(request => !request.Signed);
    }
}

public sealed class DevServerEnvironmentContainerTests(DevEnvironmentContainerFixture fixture)
    : ContainerRunTests<DevEnvironmentContainerFixture>(fixture), IClassFixture<DevEnvironmentContainerFixture>;

public sealed class SelfHostedEnvironmentContainerTests(SelfHostedEnvironmentContainerFixture fixture)
    : ContainerRunTests<SelfHostedEnvironmentContainerFixture>(fixture), IClassFixture<SelfHostedEnvironmentContainerFixture>;
