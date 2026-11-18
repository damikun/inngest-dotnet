using System.Net;
using System.Text.Json;
using Inngest.Rest;
using Inngest.Tests.Client;
using Microsoft.Extensions.Logging.Testing;

namespace Inngest.Tests.Rest;

public sealed class InngestManagementClientApiTests
{
    private readonly FakeHttpHandler _handler = new();

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Lists_apps_and_reads_the_latest_sync()
    {
        _handler
            .Respond(HttpStatusCode.OK, """
                {"data":[{"id":"shop","name":"Shop","functionCount":2,"method":"SERVE",
                          "latestSync":{"status":"success","sdkLanguage":"dotnet","syncedAt":"2026-11-12T09:00:00Z","url":"https://shop.example/api/inngest"}}],
                 "page":{"cursor":"shop","hasMore":true,"limit":1}}
                """)
            .Respond(HttpStatusCode.OK, """{"data":[],"page":{"hasMore":false,"limit":1}}""");

        var apps = await Create().ListAppsAsync(archived: true, pageSize: 1, Cancellation).CollectAsync(Cancellation);

        _handler.Requests.Select(request => request.Uri.PathAndQuery).ShouldBe(["/v2/apps?limit=1&archived=true", "/v2/apps?cursor=shop&limit=1&archived=true"]);
        var app = apps.ShouldHaveSingleItem();
        app.Id.ShouldBe("shop");
        app.FunctionCount.ShouldBe(2);
        app.Method.ShouldBe("SERVE");
        app.LatestSync.ShouldNotBeNull().SdkLanguage.ShouldBe("dotnet");
        app.LatestSync.Error.ShouldBeNull();
    }

    [Fact]
    public async Task Gets_a_function_with_its_configuration()
    {
        _handler.Respond(HttpStatusCode.OK, """
            {"data":{"id":"send-email","name":"Send email","slug":"send-email","isPaused":false,
                     "configuration":{"retries":{"value":4,"isDefault":true}},
                     "triggers":[{"type":"EVENT","value":"user/signed-up"}]}}
            """);

        var function = await Create().GetFunctionAsync("shop", "send-email", Cancellation);

        _handler.Single.Uri.AbsolutePath.ShouldBe("/v2/apps/shop/functions/send-email");
        function.Slug.ShouldBe("send-email");
        function.Configuration.GetProperty("retries").GetProperty("value").GetInt32().ShouldBe(4);
        function.Triggers.ShouldBe([new FunctionTriggerInfo("EVENT", "user/signed-up")]);
    }

    [Fact]
    public async Task Syncs_an_app_at_an_absolute_url()
    {
        _handler.Respond(HttpStatusCode.OK, """{"data":{"id":"sync-1","appId":"shop","status":"pending"}}""");

        var sync = await Create().SyncAppAsync("shop", "https://shop.example/api/inngest", Cancellation);

        _handler.Single.Method.ShouldBe(HttpMethod.Post);
        _handler.Single.Uri.AbsolutePath.ShouldBe("/v2/apps/shop/syncs");
        _handler.Single.BodyText.ShouldBe("""{"url":"https://shop.example/api/inngest"}""");
        sync.Id.ShouldBe("sync-1");
        sync.Status.ShouldBe("pending");
    }

    [Fact]
    public async Task Refuses_a_sync_url_that_is_not_absolute_http()
    {
        await Should.ThrowAsync<ArgumentException>(() => Create().SyncAppAsync("shop", "/api/inngest", Cancellation));

        _handler.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task Gets_a_run_and_asks_for_its_output()
    {
        _handler.Respond(HttpStatusCode.OK, """
            {"data":{"id":"01RUN","status":"COMPLETED","queuedAt":"2026-11-12T09:00:00Z","durationMs":"1500",
                     "function":{"id":"send-email","name":"Send email"},"app":{"id":"shop"},
                     "trigger":{"isBatch":true},"output":{"sent":true}}}
            """);

        var run = await Create().GetRunAsync("01RUN", includeOutput: true, Cancellation);

        _handler.Single.Uri.PathAndQuery.ShouldBe("/v2/runs/01RUN?include=output");
        run.RunId.ShouldBe("01RUN");
        run.FunctionId.ShouldBe("send-email");
        run.DurationMs.ShouldBe(1500);
        run.IsBatch.ShouldBeTrue();
        run.Output.GetProperty("sent").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task Cancels_a_run_with_an_empty_object()
    {
        _handler.Respond(HttpStatusCode.OK, """{"data":{"runId":"01RUN"}}""");

        await Create().CancelRunAsync("01RUN", Cancellation);

        var request = _handler.Single;
        request.Method.ShouldBe(HttpMethod.Post);
        request.Uri.AbsolutePath.ShouldBe("/v2/runs/01RUN/cancel");
        request.BodyText.ShouldBe("{}");
    }

    [Fact]
    public async Task Reruns_from_a_step_and_returns_the_new_run()
    {
        _handler.Respond(HttpStatusCode.OK, """{"data":{"runId":"01NEW"}}""");

        var id = await Create().RerunAsync("01RUN", new RerunRequest { StepId = "charge" }, Cancellation);

        id.ShouldBe("01NEW");
        _handler.Single.Uri.AbsolutePath.ShouldBe("/v2/runs/01RUN/rerun");
        _handler.Single.BodyText.ShouldBe("""{"fromStep":{"stepId":"charge"}}""");
    }

    [Fact]
    public async Task Reruns_a_step_with_a_replacement_input_array()
    {
        _handler.Respond(HttpStatusCode.OK, """{"data":{"runId":"01NEW"}}""");

        await Create().RerunAsync("01RUN", new RerunRequest { StepId = "charge", Input = JsonDocument.Parse("""[{"userId":"u-1"}]""").RootElement }, Cancellation);

        _handler.Single.BodyText.ShouldBe("""{"fromStep":{"stepId":"charge","input":[{"userId":"u-1"}]}}""");
    }

    [Fact]
    public async Task Replays_runs_of_the_given_statuses()
    {
        _handler.Respond(HttpStatusCode.OK, """
            {"data":{"id":"01REPLAY","name":"again","functionId":"send-email","from":"2026-11-12T08:00:00Z",
                     "until":"2026-11-12T09:00:00Z","statuses":["FAILED"],"createdAt":"2026-11-12T09:00:01Z"}}
            """);
        var from = new DateTimeOffset(2026, 11, 12, 8, 0, 0, TimeSpan.Zero);
        var until = new DateTimeOffset(2026, 11, 12, 9, 0, 0, TimeSpan.Zero);

        var replay = await Create().CreateReplayAsync("shop", "send-email", new ReplayRequest("again", from, until, [ReplayStatus.Failed, ReplayStatus.SkippedPaused]), Cancellation);

        _handler.Single.Uri.AbsolutePath.ShouldBe("/v2/apps/shop/functions/send-email/replays");
        using var body = JsonDocument.Parse(_handler.Single.Body);
        body.RootElement.GetProperty("statuses")[0].GetString().ShouldBe("FAILED");
        body.RootElement.GetProperty("statuses")[1].GetString().ShouldBe("SKIPPED_PAUSED");
        replay.Id.ShouldBe("01REPLAY");
        replay.Statuses.ShouldBe([ReplayStatus.Failed]);
    }

    [Fact]
    public async Task Refuses_a_replay_the_spec_rejects()
    {
        var from = new DateTimeOffset(2026, 11, 12, 8, 0, 0, TimeSpan.Zero);
        var until = new DateTimeOffset(2026, 11, 12, 9, 0, 0, TimeSpan.Zero);
        var client = Create();

        await Should.ThrowAsync<ArgumentException>(() => client.CreateReplayAsync("shop", "send-email", new ReplayRequest("no", from, until, [ReplayStatus.Failed]), Cancellation));
        await Should.ThrowAsync<ArgumentException>(() => client.CreateReplayAsync("shop", "send-email", new ReplayRequest("again", from, until, [ReplayStatus.All, ReplayStatus.Failed]), Cancellation));
        await Should.ThrowAsync<ArgumentException>(() => client.CreateReplayAsync("shop", "send-email", new ReplayRequest("again", from, until, Enumerable.Repeat(ReplayStatus.Failed, 6).ToArray()), Cancellation));

        _handler.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task Invokes_a_function_and_reads_a_run_that_is_still_going()
    {
        _handler.Respond(HttpStatusCode.Accepted, """{"data":{"runId":"01RUN","queuedAt":"2026-11-12T09:00:00Z"}}""");

        var invocation = await Create().InvokeFunctionAsync(
            "shop",
            "send-email",
            new InvokeRequest { Data = JsonDocument.Parse("""{"userId":"u-1"}""").RootElement, IdempotencyKey = "once" },
            Cancellation);

        _handler.Single.Uri.AbsolutePath.ShouldBe("/v2/apps/shop/functions/send-email/invoke");
        _handler.Single.BodyText.ShouldBe("""{"data":{"userId":"u-1"},"idempotencyKey":"once"}""");
        invocation.RunId.ShouldBe("01RUN");
        invocation.CompletedAt.ShouldBeNull();
        invocation.Result.ShouldBeNull();
    }

    [Fact]
    public async Task Records_a_score()
    {
        _handler.Respond(HttpStatusCode.OK, """{"data":[{"runId":"01RUN","name":"quality","value":0.5}]}""");

        var scores = await Create().CreateScoresAsync("01RUN", [new RunScore { Name = "quality", Value = JsonDocument.Parse("0.5").RootElement }], Cancellation);

        _handler.Single.Uri.AbsolutePath.ShouldBe("/v2/runs/01RUN/scores");
        _handler.Single.BodyText.ShouldBe("""[{"name":"quality","value":0.5}]""");
        scores.ShouldHaveSingleItem().Value.GetDouble().ShouldBe(0.5);
    }

    [Fact]
    public async Task Refuses_a_score_that_is_not_a_number_or_a_boolean()
    {
        await Should.ThrowAsync<ArgumentException>(() => Create().CreateScoresAsync("01RUN", [new RunScore { Name = "quality", Value = JsonDocument.Parse("\"no\"").RootElement }], Cancellation));

        _handler.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task Reads_a_trace_with_its_steps()
    {
        _handler.Respond(HttpStatusCode.OK, """
            {"data":{"runId":"01RUN","rootSpan":{"id":"root","name":"send-email","status":"COMPLETED","durationMs":"1500",
                     "children":[{"id":"step","name":"charge","status":"COMPLETED","stepId":"charge","stepOp":"StepRun"}]}}}
            """);

        var trace = await Create().GetRunTraceAsync("01RUN", cancellationToken: Cancellation);

        _handler.Single.Uri.PathAndQuery.ShouldBe("/v2/runs/01RUN/trace");
        trace.RunId.ShouldBe("01RUN");
        trace.Root.ShouldNotBeNull().Children.ShouldHaveSingleItem().StepId.ShouldBe("charge");
        trace.Root.DurationMs.ShouldBe(1500);
    }

    [Fact]
    public async Task Sends_one_event_and_returns_its_id()
    {
        _handler.Respond(HttpStatusCode.OK, """{"data":{"eventId":"01EVENT"}}""");
        var at = DateTimeOffset.FromUnixTimeMilliseconds(1_754_496_000_000);

        var id = await Create().SendEventAsync(new EventDraft { Name = "app/user.created", Id = "user-1", Timestamp = at }, Cancellation);

        id.ShouldBe("01EVENT");
        _handler.Single.Uri.AbsolutePath.ShouldBe("/v2/events");
        _handler.Single.BodyText.ShouldBe("""{"name":"app/user.created","id":"user-1","ts":1754496000000}""");
    }

    [Fact]
    public async Task Runs_an_insights_query()
    {
        _handler.Respond(HttpStatusCode.OK, """
            {"data":{"columns":[{"name":"name","type":"String"}],"rows":[{"values":["app/user.created"]}],
                     "diagnostics":[],"info":{"limited":false,"primaryTable":"events","tables":["events"]}}}
            """);

        var result = await Create().QueryInsightsAsync("select name from events", Cancellation);

        _handler.Single.BodyText.ShouldBe("""{"query":"select name from events"}""");
        result.Columns.ShouldHaveSingleItem().Name.ShouldBe("name");
        result.Rows.ShouldHaveSingleItem()[0].GetString().ShouldBe("app/user.created");
        result.PrimaryTable.ShouldBe("events");
    }

    [Fact]
    public async Task Creates_and_archives_an_environment()
    {
        _handler
            .Respond(HttpStatusCode.OK, """{"data":{"id":"staging","name":"Staging","type":"BRANCH","createdAt":"2026-11-12T09:00:00Z"}}""")
            .Respond(HttpStatusCode.OK, """{"data":{"id":"staging","name":"Staging","type":"BRANCH","isArchived":true,"createdAt":"2026-11-12T09:00:00Z"}}""");
        var client = Create();

        var created = await client.CreateEnvironmentAsync("Staging", "staging", Cancellation);
        var archived = await client.UpdateEnvironmentAsync("staging", archived: true, Cancellation);

        _handler.Requests[0].BodyText.ShouldBe("""{"name":"Staging","id":"staging"}""");
        _handler.Requests[1].Method.ShouldBe(HttpMethod.Patch);
        _handler.Requests[1].Uri.AbsolutePath.ShouldBe("/v2/envs/staging");
        _handler.Requests[1].BodyText.ShouldBe("""{"isArchived":true}""");
        created.Type.ShouldBe("BRANCH");
        archived.IsArchived.ShouldBeTrue();
    }

    [Fact]
    public async Task Creates_a_webhook()
    {
        _handler.Respond(HttpStatusCode.OK, """
            {"data":{"id":"wh-1","name":"Orders","url":"https://inn.gs/e/abc","environment":"production",
                     "eventFilter":{"events":["order/placed"],"filter":"ALLOW"},"createdAt":"2026-11-12T09:00:00Z","updatedAt":"2026-11-12T09:00:00Z"}}
            """);

        var webhook = await Create().CreateWebhookAsync(
            new WebhookRequest { Name = "Orders", Events = new WebhookEventFilter { Names = ["order/placed"], Filter = "ALLOW" } },
            Cancellation);

        _handler.Single.Uri.AbsolutePath.ShouldBe("/v2/env/webhooks");
        _handler.Single.BodyText.ShouldBe("""{"name":"Orders","eventFilter":{"events":["order/placed"],"filter":"ALLOW"}}""");
        webhook.Url.ShouldBe("https://inn.gs/e/abc");
        webhook.Events.ShouldNotBeNull().Names.ShouldBe(["order/placed"]);
    }

    [Fact]
    public async Task Omits_an_empty_webhook_event_filter()
    {
        _handler.Respond(HttpStatusCode.OK, """{"data":{"id":"wh-1","name":"Orders"}}""");

        await Create().CreateWebhookAsync(new WebhookRequest { Name = "Orders", Events = new WebhookEventFilter() }, Cancellation);

        _handler.Single.BodyText.ShouldBe("""{"name":"Orders"}""");
    }

    [Fact]
    public async Task Refuses_a_webhook_call_in_cloud_when_no_environment_is_set()
    {
        var client = new InngestManagementClient(new FakeHttpClientFactory(_handler), TestSettings.Cloud(), new FakeLogger<InngestManagementClient>());

        await Should.ThrowAsync<ArgumentException>(() => client.ListWebhooksAsync(cancellationToken: Cancellation).CollectAsync(Cancellation));

        _handler.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task Refuses_a_rerun_whose_step_input_is_not_an_array()
    {
        await Should.ThrowAsync<ArgumentException>(() => Create().RerunAsync("01RUN", new RerunRequest { Input = JsonDocument.Parse("{}").RootElement }, Cancellation));

        _handler.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task Refuses_an_app_reply_that_names_no_id()
    {
        _handler.Respond(HttpStatusCode.OK, """{"data":{}}""");

        await Should.ThrowAsync<InngestException>(() => Create().GetAppAsync("shop", Cancellation));
    }

    [Fact]
    public async Task Refuses_an_empty_environment_id_and_a_page_that_repeats_its_cursor()
    {
        await Should.ThrowAsync<ArgumentException>(() => Create().CreateEnvironmentAsync("Staging", string.Empty, Cancellation));

        _handler
            .Respond(HttpStatusCode.OK, """{"data":[{"id":"shop"}],"page":{"cursor":"shop","hasMore":true,"limit":1}}""")
            .Respond(HttpStatusCode.OK, """{"data":[{"id":"again"}],"page":{"cursor":"shop","hasMore":true,"limit":1}}""");

        await Should.ThrowAsync<InngestException>(() => Create().ListAppsAsync(pageSize: 1, cancellationToken: Cancellation).CollectAsync(Cancellation));
    }

    [Fact]
    public async Task Refuses_a_page_size_below_one()
    {
        await Should.ThrowAsync<ArgumentException>(() => Create().ListAppsAsync(pageSize: 0, cancellationToken: Cancellation).CollectAsync(Cancellation));

        _handler.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task Lists_the_runs_of_a_session()
    {
        _handler.Respond(HttpStatusCode.OK, """
            {"data":[{"id":"01RUN","status":"FAILED","eventName":"order/placed","queuedAt":"2026-11-12T09:00:00Z",
                      "function":{"id":"send-email","name":"Send email"}}],"page":{"hasMore":false,"limit":40}}
            """);

        var runs = await Create().ListSessionRunsAsync("customer", "c-1", cancellationToken: Cancellation).CollectAsync(Cancellation);

        _handler.Single.Uri.PathAndQuery.ShouldBe("/v2/sessions/customer/c-1/runs");
        var run = runs.ShouldHaveSingleItem();
        run.Status.ShouldBe(FunctionRunStatus.Failed);
        run.FunctionId.ShouldBe("send-email");
        run.EventName.ShouldBe("order/placed");
    }

    [Fact]
    public async Task A_client_that_predates_these_methods_fails_when_one_is_called()
    {
        IInngestManagementClient client = new OriginalClient();

        await Should.ThrowAsync<NotSupportedException>(() => client.GetAccountAsync(Cancellation));
    }

    private InngestManagementClient Create() =>
        new(new FakeHttpClientFactory(_handler), TestSettings.Create(), new FakeLogger<InngestManagementClient>());

    private sealed class OriginalClient : IInngestManagementClient
    {
        public Task<FunctionInfo> PauseFunctionAsync(string appId, string functionId, CancellationToken cancellationToken = default) => throw new NotImplementedException();

        public Task<FunctionInfo> ResumeFunctionAsync(string appId, string functionId, CancellationToken cancellationToken = default) => throw new NotImplementedException();

        public Task<IReadOnlyList<FunctionPauseResult>> PauseFunctionsAsync(string appId, IReadOnlyList<string> functionIds, CancellationToken cancellationToken = default) => throw new NotImplementedException();

        public Task<IReadOnlyList<FunctionPauseResult>> ResumeFunctionsAsync(string appId, IReadOnlyList<string> functionIds, CancellationToken cancellationToken = default) => throw new NotImplementedException();

        public Task<RunCancellation> CancelRunsAsync(string appId, string functionId, CancelRunsRequest request, CancellationToken cancellationToken = default) => throw new NotImplementedException();

        public IAsyncEnumerable<EventInfo> ListEventsAsync(EventQuery? query = null, CancellationToken cancellationToken = default) => throw new NotImplementedException();

        public Task<EventInfo> GetEventAsync(string eventId, bool includePayload = false, CancellationToken cancellationToken = default) => throw new NotImplementedException();

        public IAsyncEnumerable<AppSync> ListAppSyncsAsync(string appId, int? pageSize = null, CancellationToken cancellationToken = default) => throw new NotImplementedException();

        public IAsyncEnumerable<RunSummary> ListRunsAsync(RunQuery? query = null, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    }
}

file static class ManagementApiEnumerable
{
    public static async Task<List<T>> CollectAsync<T>(this IAsyncEnumerable<T> source, CancellationToken cancellationToken)
    {
        var items = new List<T>();
        await foreach (var item in source.WithCancellation(cancellationToken))
        {
            items.Add(item);
        }

        return items;
    }
}
