using System.Net;
using System.Text.Json;
using Inngest.Configuration;
using Inngest.Rest;
using Inngest.Signing;
using Inngest.Tests.Client;
using Microsoft.Extensions.Logging.Testing;

namespace Inngest.Tests.Rest;

// The v2 REST API as the Inngest v1.52.0 server answers it: camelCase names, false and empty values left out of most
// replies, 64-bit numbers as strings and enums by their short names.
public sealed class InngestManagementClientTests
{
    private const string Key = "signkey-prod-0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    private const string Function = """
        {"id":"send-email","name":"Send email","slug":"send-email","isPaused":true,"app":{"id":"shop"},
         "triggers":[{"type":"EVENT","value":"user/signed-up","if":"event.data.plan == 'pro'"},{"type":"CRON","value":"0 9 * * *"}],
         "configuration":{"retries":{"value":4,"isDefault":true}}}
        """;

    private const string Run = """
        {"id":"01M61VV20X89FZAW14GA30NNNN","function":{"id":"send-email","name":"Send email","slug":"send-email"},
         "app":{"id":"shop"},"status":"COMPLETED","queuedAt":"2026-11-12T09:00:00Z","startedAt":"2026-11-12T09:00:00.25Z",
         "endedAt":"2026-11-12T09:00:01.75Z","durationMs":"1500",
         "trigger":{"eventIds":["01M61VV209SMJYG2RA0687EJY1"],"eventName":"user/signed-up"},"output":{"sent":true}}
        """;

    private readonly FakeHttpHandler _handler = new();

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Pauses_a_function_with_a_post_that_has_no_body()
    {
        _handler.Respond(HttpStatusCode.OK, $$$"""{"data":{{{Function}}},"metadata":{"fetchedAt":"2026-11-12T09:00:00Z"}}""");

        var function = await Create().PauseFunctionAsync("shop", "send-email", Cancellation);

        var request = _handler.Single;
        request.Method.ShouldBe(HttpMethod.Post);
        request.Uri.ShouldBe(new Uri("http://localhost:8288/v2/apps/shop/functions/send-email/pause"));
        request.Body.ShouldBeEmpty();
        function.Id.ShouldBe("send-email");
        function.Name.ShouldBe("Send email");
        function.AppId.ShouldBe("shop");
        function.IsPaused.ShouldBeTrue();
        function.IsArchived.ShouldBeFalse();
        function.Triggers.ShouldBe([new FunctionTriggerInfo("EVENT", "user/signed-up", "event.data.plan == 'pro'"), new FunctionTriggerInfo("CRON", "0 9 * * *")]);
    }

    [Fact]
    public async Task Resumes_a_function_and_reads_a_left_out_flag_as_false()
    {
        _handler.Respond(HttpStatusCode.OK, """{"data":{"id":"send-email","name":"Send email","app":{"id":"shop"}}}""");

        var function = await Create().ResumeFunctionAsync("shop", "send-email", Cancellation);

        _handler.Single.Uri.AbsolutePath.ShouldBe("/v2/apps/shop/functions/send-email/resume");
        function.IsPaused.ShouldBeFalse();
        function.IsArchived.ShouldBeFalse();
        function.Triggers.ShouldBeEmpty();
    }

    [Fact]
    public async Task Escapes_the_app_and_function_ids_in_the_path()
    {
        _handler.Respond(HttpStatusCode.OK, """{"data":{"id":"a b"}}""");

        await Create().PauseFunctionAsync("my app", "a b", Cancellation);

        _handler.Single.Uri.AbsoluteUri.ShouldBe("http://localhost:8288/v2/apps/my%20app/functions/a%20b/pause");
    }

    [Fact]
    public async Task Pauses_several_functions_and_reports_each_outcome_in_order()
    {
        _handler.Respond(HttpStatusCode.OK, $$$"""
            {"data":[{"functionId":"send-email","function":{{{Function}}}},
                     {"functionId":"gone","error":{"code":"not_found","message":"Function not found"}}],
             "metadata":{"fetchedAt":"2026-11-12T09:00:00Z"}}
            """);

        var results = await Create().PauseFunctionsAsync("shop", ["send-email", "gone"], Cancellation);

        _handler.Single.Uri.AbsolutePath.ShouldBe("/v2/apps/shop/functions/pause");
        _handler.Single.BodyText.ShouldBe("""{"functionIds":["send-email","gone"]}""");
        results.Count.ShouldBe(2);
        results[0].Succeeded.ShouldBeTrue();
        results[0].Function.ShouldNotBeNull().IsPaused.ShouldBeTrue();
        results[1].ShouldBe(new FunctionPauseResult { FunctionId = "gone", Error = new InngestApiError("not_found", "Function not found") });
        results[1].Succeeded.ShouldBeFalse();
    }

    [Fact]
    public async Task Resumes_several_functions_at_their_own_endpoint()
    {
        _handler.Respond(HttpStatusCode.OK, """{"data":[{"functionId":"a","function":{"id":"a"}}]}""");

        var results = await Create().ResumeFunctionsAsync("shop", ["a"], Cancellation);

        _handler.Single.Uri.AbsolutePath.ShouldBe("/v2/apps/shop/functions/resume");
        results.ShouldHaveSingleItem().Function!.Id.ShouldBe("a");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(501)]
    public async Task Refuses_a_number_of_function_ids_that_inngest_refuses(int count)
    {
        var ids = Enumerable.Range(0, count).Select(i => $"f{i}").ToArray();

        await Should.ThrowAsync<ArgumentException>(() => Create().PauseFunctionsAsync("shop", ids, Cancellation));

        _handler.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task Cancels_the_runs_of_a_function_in_a_time_range_with_times_in_utc()
    {
        _handler.Respond(HttpStatusCode.Created, """
            {"data":{"id":"01KCANCEL0000000000000000A","appId":"shop","functionId":"send-email","name":"cleanup",
             "from":"2026-11-12T08:00:00Z","until":"2026-11-12T09:00:00Z","if":"event.data.userId == 'u-1'",
             "createdAt":"2026-11-12T09:00:01Z"}}
            """);

        var cancellation = await Create().CancelRunsAsync(
            "shop",
            "send-email",
            new CancelRunsRequest(new DateTimeOffset(2026, 11, 12, 10, 0, 0, TimeSpan.FromHours(2)), new DateTimeOffset(2026, 11, 12, 9, 0, 0, TimeSpan.Zero))
            {
                If = "event.data.userId == 'u-1'",
                Name = "cleanup",
            },
            Cancellation);

        var request = _handler.Single;
        request.Method.ShouldBe(HttpMethod.Post);
        request.Uri.AbsolutePath.ShouldBe("/v2/apps/shop/functions/send-email/cancellations");
        using (var body = JsonDocument.Parse(request.Body))
        {
            body.RootElement.EnumerateObject().Select(property => (property.Name, property.Value.GetString())).ShouldBe([
                ("from", "2026-11-12T08:00:00Z"), ("until", "2026-11-12T09:00:00Z"), ("if", "event.data.userId == 'u-1'"), ("name", "cleanup")]);
        }

        request.Header("Content-Type").ShouldBe("application/json");
        cancellation.ShouldBe(new RunCancellation
        {
            Id = "01KCANCEL0000000000000000A",
            AppId = "shop",
            FunctionId = "send-email",
            Name = "cleanup",
            From = new DateTimeOffset(2026, 11, 12, 8, 0, 0, TimeSpan.Zero),
            Until = new DateTimeOffset(2026, 11, 12, 9, 0, 0, TimeSpan.Zero),
            If = "event.data.userId == 'u-1'",
            CreatedAt = new DateTimeOffset(2026, 11, 12, 9, 0, 1, TimeSpan.Zero),
        });
    }

    [Fact]
    public async Task Sends_neither_an_expression_nor_a_name_that_is_not_given()
    {
        _handler.Respond(HttpStatusCode.Created, """{"data":{"id":"c1"}}""");
        var at = new DateTimeOffset(2026, 11, 12, 9, 0, 0, TimeSpan.Zero);

        await Create().CancelRunsAsync("shop", "send-email", new CancelRunsRequest(at, at), Cancellation);

        _handler.Single.BodyText.ShouldBe("""{"from":"2026-11-12T09:00:00Z","until":"2026-11-12T09:00:00Z"}""");
    }

    [Fact]
    public async Task Refuses_a_time_range_that_ends_before_it_starts()
    {
        var at = new DateTimeOffset(2026, 11, 12, 9, 0, 0, TimeSpan.Zero);

        await Should.ThrowAsync<ArgumentException>(() => Create().CancelRunsAsync("shop", "f", new CancelRunsRequest(at, at.AddSeconds(-1)), Cancellation));
    }

    [Fact]
    public async Task Lists_events_page_by_page_in_the_time_range_of_the_first_page()
    {
        _handler
            .Respond(HttpStatusCode.OK, """
                {"data":[{"id":"E2","name":"user/signed-up","receivedAt":"2026-11-12T09:00:02Z","idempotencyKey":"","version":""}],
                 "metadata":{"fetchedAt":"2026-11-12T09:00:05Z","timeRange":{"from":"2026-11-12T08:00:05Z","until":"2026-11-12T09:00:05Z"}},
                 "page":{"cursor":"E2","hasMore":true,"limit":1}}
                """)
            .Respond(HttpStatusCode.OK, """
                {"data":[{"id":"E1","name":"user/signed-up","receivedAt":"2026-11-12T09:00:01Z","occurredAt":"2026-11-12T09:00:00Z",
                          "idempotencyKey":"signup-u-1","version":"2026-01-01"}],
                 "metadata":{"fetchedAt":"2026-11-12T09:00:06Z","timeRange":{"from":"2026-11-12T08:00:05Z","until":"2026-11-12T09:00:05Z"}},
                 "page":{"hasMore":false,"limit":1}}
                """);

        var events = await Create().ListEventsAsync(new EventQuery { Names = ["user/signed-up", "user/left"], PageSize = 1 }, Cancellation).ToListAsync(Cancellation);

        events.Select(e => e.Id).ShouldBe(["E2", "E1"]);
        events[0].IdempotencyKey.ShouldBeNull();
        events[0].Version.ShouldBeNull();
        events[0].OccurredAt.ShouldBeNull();
        events[0].Payload.ValueKind.ShouldBe(JsonValueKind.Undefined);
        events[1].IdempotencyKey.ShouldBe("signup-u-1");
        events[1].OccurredAt.ShouldBe(new DateTimeOffset(2026, 11, 12, 9, 0, 0, TimeSpan.Zero));
        _handler.Requests.Select(request => request.Uri.PathAndQuery).ShouldBe([
            "/v2/events?limit=1&name=user%2Fsigned-up&name=user%2Fleft",
            "/v2/events?cursor=E2&limit=1&from=2026-11-12T08%3A00%3A05Z&until=2026-11-12T09%3A00%3A05Z&name=user%2Fsigned-up&name=user%2Fleft",
        ]);
    }

    [Fact]
    public async Task Gets_an_event_with_its_payload()
    {
        _handler.Respond(HttpStatusCode.OK, """
            {"data":{"id":"E1","name":"user/signed-up","receivedAt":"2026-11-12T09:00:01Z","idempotencyKey":"",
             "payload":{"name":"user/signed-up","data":{"userId":"u-1"},"ts":1794474001000}}}
            """);

        var received = await Create().GetEventAsync("E1", includePayload: true, Cancellation);

        _handler.Single.Uri.PathAndQuery.ShouldBe("/v2/events/E1?include=payload");
        received.Name.ShouldBe("user/signed-up");
        received.Payload.GetProperty("data").GetProperty("userId").GetString().ShouldBe("u-1");
    }

    [Fact]
    public async Task Lists_runs_with_the_query_as_inngest_names_it()
    {
        _handler.Respond(HttpStatusCode.OK, $$$"""{"data":[{{{Run}}}],"metadata":{"fetchedAt":"2026-11-12T09:00:05Z"},"page":{"hasMore":false,"limit":100}}""");

        var runs = await Create().ListRunsAsync(
            new RunQuery
            {
                From = new DateTimeOffset(2026, 11, 12, 8, 0, 0, TimeSpan.Zero),
                Until = new DateTimeOffset(2026, 11, 12, 9, 0, 0, 500, TimeSpan.Zero),
                TimeField = RunTimeField.EndedAt,
                Statuses = [FunctionRunStatus.Completed, FunctionRunStatus.Scheduled],
                AppIds = ["shop"],
                FunctionIds = ["send-email"],
                Query = "output.sent == true",
                Ascending = true,
                IncludeOutput = true,
                PageSize = 100,
            },
            Cancellation).ToListAsync(Cancellation);

        _handler.Single.Uri.PathAndQuery.ShouldBe(
            "/v2/runs?limit=100&from=2026-11-12T08%3A00%3A00Z&until=2026-11-12T09%3A00%3A00.5Z&timeField=endedAt&status=COMPLETED&status=QUEUED" +
            "&appId=shop&functionId=send-email&query=output.sent%20%3D%3D%20true&order=ASC&include=output");
        var run = runs.ShouldHaveSingleItem();
        run.RunId.ShouldBe("01M61VV20X89FZAW14GA30NNNN");
        run.Status.ShouldBe(FunctionRunStatus.Completed);
        run.FunctionId.ShouldBe("send-email");
        run.FunctionName.ShouldBe("Send email");
        run.AppId.ShouldBe("shop");
        run.QueuedAt.ShouldBe(new DateTimeOffset(2026, 11, 12, 9, 0, 0, TimeSpan.Zero));
        run.StartedAt.ShouldBe(new DateTimeOffset(2026, 11, 12, 9, 0, 0, 250, TimeSpan.Zero));
        run.EndedAt.ShouldBe(new DateTimeOffset(2026, 11, 12, 9, 0, 1, 750, TimeSpan.Zero));
        run.EventIds.ShouldBe(["01M61VV209SMJYG2RA0687EJY1"]);
        run.EventName.ShouldBe("user/signed-up");
        run.BatchId.ShouldBeNull();
        run.Cron.ShouldBeNull();
        run.Output.GetProperty("sent").GetBoolean().ShouldBeTrue();
    }

    [Theory]
    [InlineData("QUEUED", FunctionRunStatus.Scheduled)]
    [InlineData("RUNNING", FunctionRunStatus.Running)]
    [InlineData("PAUSED", FunctionRunStatus.Paused)]
    [InlineData("FAILED", FunctionRunStatus.Failed)]
    [InlineData("CANCELLED", FunctionRunStatus.Cancelled)]
    [InlineData("SKIPPED", FunctionRunStatus.Skipped)]
    [InlineData("SOMETHING_NEW", FunctionRunStatus.Unknown)]
    public async Task Reads_each_run_status(string status, FunctionRunStatus expected)
    {
        _handler.Respond(HttpStatusCode.OK, $$$"""{"data":[{"id":"r1","status":"{{{status}}}","queuedAt":"2026-11-12T09:00:00Z"}],"page":{"limit":20}}""");

        var runs = await Create().ListRunsAsync(cancellationToken: Cancellation).ToListAsync(Cancellation);

        runs.ShouldHaveSingleItem().Status.ShouldBe(expected);
        runs[0].EndedAt.ShouldBeNull();
        runs[0].Output.ValueKind.ShouldBe(JsonValueKind.Undefined);
    }

    [Fact]
    public async Task Follows_the_cursor_of_the_run_list_and_stops_at_an_empty_page()
    {
        _handler
            .Respond(HttpStatusCode.OK, $$$"""{"data":[{{{Run}}}],"page":{"cursor":"c1","hasMore":true,"limit":1}}""")
            .Respond(HttpStatusCode.OK, """{"page":{"limit":1}}""");

        var runs = await Create().ListRunsAsync(new RunQuery { PageSize = 1 }, Cancellation).ToListAsync(Cancellation);

        runs.Count.ShouldBe(1);
        _handler.Requests.Select(request => request.Uri.PathAndQuery).ShouldBe(["/v2/runs?limit=1", "/v2/runs?cursor=c1&limit=1"]);
    }

    [Fact]
    public async Task Refuses_a_function_filter_without_an_app()
    {
        await Should.ThrowAsync<ArgumentException>(async () =>
            await Create().ListRunsAsync(new RunQuery { FunctionIds = ["send-email"] }, Cancellation).ToListAsync(Cancellation));
    }

    [Fact]
    public async Task Lists_the_syncs_of_an_app()
    {
        _handler.Respond(HttpStatusCode.OK, """
            {"data":[{"id":"7b0c9f2e-0000-4000-8000-000000000001","appId":"shop","status":"error","url":"https://shop.example.com/api/inngest",
                      "sdkLanguage":"dotnet","sdkVersion":"1.1.0","framework":"aspnetcore","appVersion":"",
                      "error":{"code":"unreachable","message":"Could not reach the app"},"createdAt":"2026-11-12T09:00:00Z"}],
             "metadata":{"fetchedAt":"2026-11-12T09:00:05Z"},"page":{"hasMore":false,"limit":20}}
            """);

        var syncs = await Create().ListAppSyncsAsync("shop", cancellationToken: Cancellation).ToListAsync(Cancellation);

        _handler.Single.Uri.PathAndQuery.ShouldBe("/v2/apps/shop/syncs");
        var sync = syncs.ShouldHaveSingleItem();
        sync.Status.ShouldBe("error");
        sync.SdkLanguage.ShouldBe("dotnet");
        sync.Error.ShouldBe(new InngestApiError("unreachable", "Could not reach the app"));
    }

    [Fact]
    public async Task Fails_with_the_message_and_code_inngest_gives()
    {
        _handler.Respond(HttpStatusCode.NotImplemented, """{"errors":[{"code":"not_implemented","message":"List app syncs is not yet implemented"}]}""");

        var exception = await Should.ThrowAsync<InngestException>(async () =>
            await Create().ListAppSyncsAsync("shop", cancellationToken: Cancellation).ToListAsync(Cancellation));

        exception.Message.ShouldBe("Failed to list the syncs of app shop: List app syncs is not yet implemented (not_implemented)");
        exception.StatusCode.ShouldBe(501);
    }

    [Fact]
    public async Task Fails_with_the_text_a_self_hosted_server_answers_to_a_wrong_key()
    {
        _handler.Respond(HttpStatusCode.Unauthorized, "Authentication failed\n", "text/plain");

        var exception = await Should.ThrowAsync<InngestException>(() => Create().GetEventAsync("E1", cancellationToken: Cancellation));

        exception.Message.ShouldBe("Failed to get event E1: Authentication failed");
        exception.StatusCode.ShouldBe(401);
    }

    [Fact]
    public async Task Authenticates_with_the_hashed_signing_key_and_names_the_environment()
    {
        _handler.Respond(HttpStatusCode.OK, """{"data":{"id":"E1"}}""");
        var client = Create(TestSettings.Cloud(options =>
        {
            options.SigningKey = Key;
            options.Environment = "branch-1";
        }));

        await client.GetEventAsync("E1", cancellationToken: Cancellation);

        var request = _handler.Single;
        request.Uri.ShouldBe(new Uri("https://api.inngest.com/v2/events/E1"));
        request.Header("Authorization").ShouldBe("Bearer " + SigningKey.Create(Key)!.BearerToken);
        request.Header("X-Inngest-Env").ShouldBe("branch-1");
    }

    private InngestManagementClient Create(InngestSettings? settings = null) =>
        new(new FakeHttpClientFactory(_handler), settings ?? TestSettings.Create(), new FakeLogger<InngestManagementClient>());
}
