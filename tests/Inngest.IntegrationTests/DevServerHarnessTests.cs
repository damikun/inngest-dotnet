using System.Net;
using System.Text.Json;
using Inngest.IntegrationTests.Harness;

namespace Inngest.IntegrationTests;

// Checks of the harness itself that do not involve the SDK under test: the server starts and reaches the app on the
// host, events go in, and the run, trace and function helpers read real server responses correctly. Runs come from a
// fake SDK, so these pass (or fail) on their own when a test of the SDK fails.
public sealed class DevServerHarnessTests(DevServerFixture fixture) : IntegrationTest<DevServerFixture>(fixture)
{
    // Must not depend on the SDK syncing.
    public override ValueTask InitializeAsync() => ValueTask.CompletedTask;

    [Fact(Timeout = 30_000)]
    public async Task Server_is_healthy()
    {
        using var http = new HttpClient { BaseAddress = Api.BaseUrl };
        using var response = await http.GetAsync("health", Cancellation);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Headers.GetValues("X-Inngest-Server-Kind").ShouldBe(["dev"]);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Cancellation));
        body.RootElement.ShouldContainJson("""{ "status": 200, "message": "OK" }""");
    }

    [Fact(Timeout = 30_000)]
    public async Task Server_reaches_the_app_on_the_host_and_asks_it_to_sync()
    {
        var sync = await Poll.UntilAsync(
            "the dev server's sync request to reach the app",
            _ => Fixture.App.Requests.Snapshot().FirstOrDefault(request => request.Method == "PUT"),
            request => request is not null,
            TimeSpan.FromSeconds(10),
            Cancellation);

        sync!.Target.ShouldBe(TestApp.ServePath);
        sync.ServerKind.ShouldBe("dev");
        sync.Signed.ShouldBeFalse();
        using var body = JsonDocument.Parse(sync.Body.ShouldNotBeNull());
        body.RootElement.ShouldContainJson($$"""{ "url": "{{Fixture.App.ServerFacingUrl}}" }""");
    }

    [Fact(Timeout = 30_000)]
    public async Task Sending_events_returns_one_id_per_event()
    {
        var ids = await Api.SendEventsAsync(
            [new EventPayload("harness/ping", new { key = TestId }), new EventPayload("harness/ping", new { key = TestId })],
            Cancellation);

        ids.Count.ShouldBe(2);
        ids.ShouldAllBe(id => id.Length == 26);
        ids.Distinct().Count().ShouldBe(2);
    }

    [Fact(Timeout = 30_000)]
    public async Task Function_helpers_read_back_what_an_app_registered()
    {
        await using var fake = await FakeSdk.StartAsync(Api, Cancellation);

        var functions = await Api.GetAppFunctionsAsync(fake.AppId, Cancellation);

        functions.Select(function => function.Slug).ShouldBe(fake.Slugs, ignoreOrder: true);
        var sleep = functions.Single(function => function.Slug == fake.Slug("sleep"));
        sleep.Name.ShouldBe("sleep");
        sleep.StepUrl.ShouldBe($"{fake.Url}?fnId={fake.Slug("sleep")}&stepId=step");
        sleep.Retries.ShouldBeNull();
        sleep.Config.ShouldContainJson($$"""
            {
              "triggers": [{ "event": "{{fake.EventName("sleep")}}" }],
              "cancel": [{ "event": "{{fake.EventName("cancel")}}", "if": "event.data.key == async.data.key" }]
            }
            """);

        var app = (await Api.GetAppsAsync(Cancellation)).Single(app => app.ExternalId == fake.AppId);
        app.ShouldBe(new RegisteredApp(fake.AppId, fake.Url.AbsoluteUri, null, 4, "fake", "v0.0.1", "fake"));
    }

    [Fact(Timeout = 30_000)]
    public async Task Run_helpers_follow_a_run_to_its_output()
    {
        await using var fake = await FakeSdk.StartAsync(Api, Cancellation);
        var eventId = await Api.SendEventAsync(fake.EventName("echo"), new { key = TestId }, Cancellation);

        var run = await Api.WaitForRunEndAsync(eventId, Cancellation);

        run.Status.ShouldBe(RunStatus.Completed);
        run.EventId.ShouldBe(eventId);
        run.EndedAt.ShouldNotBeNull().ShouldBeGreaterThan(DateTimeOffset.UtcNow.AddMinutes(-1));
        run.Output.ShouldNotBeNull().ShouldContainJson($$"""{ "echo": { "key": "{{TestId}}" } }""");
        (await Api.GetEventRunsAsync(eventId, Cancellation)).ShouldHaveSingleItem().RunId.ShouldBe(run.RunId);

        // GraphQL catches up with the REST API a moment later.
        var trace = await Api.WaitForTraceAsync(run.RunId, "the run completed", trace => trace.Status == "COMPLETED", Cancellation);
        trace.Spans.ShouldNotBeEmpty();
    }

    [Fact(Timeout = 30_000)]
    public async Task Run_helpers_report_a_failed_run_with_its_error()
    {
        await using var fake = await FakeSdk.StartAsync(Api, Cancellation);

        var run = await RunToEndAsync(fake.EventName("fail"), new { key = TestId });

        run.Status.ShouldBe(RunStatus.Failed);
        run.Error.Name.ShouldBe("Error");
        run.Error.Message.ShouldBe("The fake failed");
    }

    [Fact(Timeout = 30_000)]
    public async Task Step_helpers_see_a_wait_that_a_matching_event_resumes()
    {
        await using var fake = await FakeSdk.StartAsync(Api, Cancellation);
        var eventId = await Api.SendEventAsync(fake.EventName("wait"), new { key = TestId }, Cancellation);
        var run = await Api.WaitForRunAsync(eventId, Cancellation);

        await Api.WaitForStepAsync(run.RunId, StepOps.WaitForEvent, SpanStatuses.Waiting, Cancellation);
        await Api.SendEventAsync(fake.EventName("resume"), new { key = TestId, message = "resume!" }, Cancellation);
        var ended = await Api.WaitForRunEndAsync(eventId, Cancellation);

        ended.Status.ShouldBe(RunStatus.Completed);
        ended.Output.ShouldNotBeNull().ShouldContainJson($$"""
            { "resumed": { "name": "{{fake.EventName("resume")}}", "data": { "key": "{{TestId}}", "message": "resume!" } } }
            """);
    }

    [Fact(Timeout = 30_000)]
    public async Task Step_helpers_see_a_sleep_that_a_cancel_event_ends()
    {
        await using var fake = await FakeSdk.StartAsync(Api, Cancellation);
        var eventId = await Api.SendEventAsync(fake.EventName("sleep"), new { key = TestId }, Cancellation);
        var run = await Api.WaitForRunAsync(eventId, Cancellation);

        await Api.WaitForStepAsync(run.RunId, StepOps.Sleep, SpanStatuses.Waiting, Cancellation);
        await Api.SendEventAsync(fake.EventName("cancel"), new { key = TestId }, Cancellation);
        var ended = await Api.WaitForRunEndAsync(eventId, Cancellation);

        ended.Status.ShouldBe(RunStatus.Cancelled);
    }

    [Fact(Timeout = 30_000)]
    public async Task Waits_that_time_out_say_what_they_waited_for_and_include_the_server_log()
    {
        var eventId = await Api.SendEventAsync("harness/nobody.listens", new { key = TestId }, Cancellation);

        var exception = await Should.ThrowAsync<TimeoutException>(
            () => Api.WaitForRunAsync(eventId, Cancellation, TimeSpan.FromSeconds(1)));

        exception.Message.ShouldContain($"event {eventId} to start a run");
        exception.Message.ShouldContain("--- Inngest server log tail ---");
        exception.Message.ShouldContain(eventId);
    }
}
