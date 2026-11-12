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
        var runs = await Fixture.Server.SendAndWaitAsync(new InngestEvent("containers/hello", new { name = "Ada" }), cancellationToken: Cancellation);

        var run = runs.ShouldHaveSingleItem();
        run.Status.ShouldBe(FunctionRunStatus.Completed);
        run.EndedAt.ShouldNotBeNull();
        run.GetOutput<Hello>().ShouldBe(new Hello("Hello, Ada!"));
    }

    [Fact(Timeout = 60_000)]
    public async Task A_function_with_steps_is_called_by_the_server_once_for_each_step()
    {
        var runs = await Fixture.Server.SendAndWaitAsync(new InngestEvent("containers/steps", new { value = 20 }), cancellationToken: Cancellation);

        var run = runs.ShouldHaveSingleItem();
        run.Status.ShouldBe(FunctionRunStatus.Completed);
        run.Output.ShouldContainJson("""{ "result": 42 }""");
        Fixture.App.Requests.Snapshot().Count(request => request.Method == "POST").ShouldBeGreaterThanOrEqualTo(3);
    }

    [Fact(Timeout = 60_000)]
    public async Task Send_and_wait_times_out_when_the_event_starts_fewer_runs_than_expected()
    {
        var exception = await Should.ThrowAsync<TimeoutException>(() => Fixture.Server.SendAndWaitAsync(
            new InngestEvent("containers/hello", new { name = "Edsger" }), expectedRuns: 2, TimeSpan.FromSeconds(3), Cancellation));

        exception.Message.ShouldContain("did not start 2 run(s) that end within 3 s");
        exception.Message.ShouldContain("Completed (ended)");
    }

    [Fact(Timeout = 60_000)]
    public async Task A_client_of_the_server_settles_quickly_on_an_event_that_starts_no_run()
    {
        await using var client = Fixture.Server.CreateClient();
        var sent = await client.Events.SendAsync(new InngestEvent("containers/nobody-listens", new { name = "Barbara" }), Cancellation);

        // Every read is fresh, so the list settles after the settle time, not after the server's 15 s cache.
        var started = TimeProvider.System.GetTimestamp();
        var runs = await client.Runs.WaitForEventRunsToSettleAsync(sent.Ids[0], TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30), Cancellation);

        runs.ShouldBeEmpty();
        TimeProvider.System.GetElapsedTime(started).ShouldBeLessThan(TimeSpan.FromSeconds(10));
    }

    [Fact(Timeout = 60_000)]
    public async Task The_app_s_own_client_sends_events_to_the_container()
    {
        var client = Fixture.App.Services.GetRequiredService<IInngestClient>();

        var sent = await client.SendAsync(new InngestEvent("containers/hello", new { name = "Grace" }), Cancellation);
        await using var server = Fixture.Server.CreateClient();
        var runs = await server.Runs.WaitForEventRunsAsync(sent.Ids.Single(), 1, TimeSpan.FromSeconds(30), Cancellation);

        runs.ShouldHaveSingleItem().GetOutput<Hello>().ShouldBe(new Hello("Hello, Grace!"));
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
    public async Task Syncing_an_address_that_is_no_inngest_endpoint_says_what_the_app_answered()
    {
        var elsewhere = new Uri($"http://127.0.0.1:{Fixture.App.Port}/not-the-inngest-endpoint");

        var exception = await Should.ThrowAsync<InvalidOperationException>(() => Fixture.Server.SyncAsync(elsewhere, Cancellation));

        exception.Message.ShouldContain("404");
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
    public void The_sync_request_says_it_comes_from_a_dev_server_like_the_servers_own()
    {
        var sync = Fixture.App.Requests.Snapshot().First(request => request.Method == "PUT");

        sync.ServerKind.ShouldBe("dev");
    }

    [Fact(Timeout = 60_000)]
    public void The_dev_server_neither_signs_its_requests_nor_is_asked_to()
    {
        Fixture.App.Requests.Snapshot().ShouldNotBeEmpty();
        Fixture.App.Requests.Snapshot().ShouldAllBe(request => !request.Signed);
    }
}

// What the hello function returns.
public sealed record Hello(string Greeting);
