using System.Net;
using System.Text.Json;
using Inngest.Configuration;
using Inngest.Rest;
using Inngest.Signing;
using Inngest.Tests.Client;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Testing;

namespace Inngest.Tests.Rest;

public sealed class InngestRunsClientTests
{
    private const string Key = "signkey-prod-0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    // A run as the dev server returns it, including the nanoseconds of its end time.
    private const string CompletedRun = """
        {"run_id":"01M61VV20X89FZAW14GA30NNNN","run_started_at":"2026-10-28T18:19:58.877Z",
         "function_id":"2b6a2c8e-8752-56c4-bafe-1d61d4780711","function_version":3,
         "environment_id":"00000000-0000-4000-b000-000000000000","event_id":"01M61VV209SMJYG2RA0687EJY1",
         "batch_id":null,"original_run_id":"01M61VV20X89FZAW14GA30AAAA","cron":null,
         "status":"Completed","ended_at":"2026-10-28T18:19:58.896324375Z","output":{"greeting":"hi dalibor"}}
        """;

    private const string Metadata = """
        "metadata":{"fetched_at":"2026-10-28T18:20:10.247932713Z","cached_until":"2026-10-28T18:20:13.247933005Z"}
        """;

    private readonly FakeHttpHandler _handler = new();

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Gets_a_run_with_its_status_times_and_output()
    {
        _handler.Respond(HttpStatusCode.OK, $$"""{"data":{{CompletedRun}},{{Metadata}}}""");

        var run = await Create().GetRunAsync("01M61VV20X89FZAW14GA30NNNN", Cancellation);

        _handler.Single.Method.ShouldBe(HttpMethod.Get);
        _handler.Single.Uri.ShouldBe(new Uri("http://localhost:8288/v1/runs/01M61VV20X89FZAW14GA30NNNN"));
        run.RunId.ShouldBe("01M61VV20X89FZAW14GA30NNNN");
        run.Status.ShouldBe(FunctionRunStatus.Completed);
        run.FunctionId.ShouldBe("2b6a2c8e-8752-56c4-bafe-1d61d4780711");
        run.FunctionVersion.ShouldBe(3);
        run.StartedAt.ShouldBe(new DateTimeOffset(2026, 10, 28, 18, 19, 58, 877, TimeSpan.Zero));
        run.EndedAt.ShouldBe(new DateTimeOffset(2026, 10, 28, 18, 19, 58, 896, TimeSpan.Zero).AddTicks(3243));
        run.EventId.ShouldBe("01M61VV209SMJYG2RA0687EJY1");
        run.BatchId.ShouldBeNull();
        run.OriginalRunId.ShouldBe("01M61VV20X89FZAW14GA30AAAA");
        run.Cron.ShouldBeNull();
        run.EnvironmentId.ShouldBe("00000000-0000-4000-b000-000000000000");
        run.Output.GetProperty("greeting").GetString().ShouldBe("hi dalibor");
        run.GetOutput<Hello>().ShouldBe(new Hello("hi dalibor"));
    }

    [Fact]
    public async Task Authenticates_with_the_hashed_signing_key_and_names_the_environment()
    {
        _handler.Respond(HttpStatusCode.OK, $$"""{"data":{{CompletedRun}}}""");
        var client = Create(TestSettings.Cloud(options =>
        {
            options.SigningKey = Key;
            options.Environment = "branch-1";
        }));

        await client.GetRunAsync("01M61VV20X89FZAW14GA30NNNN", Cancellation);

        var request = _handler.Single;
        request.Uri.ShouldBe(new Uri("https://api.inngest.com/v1/runs/01M61VV20X89FZAW14GA30NNNN"));
        request.Header("Authorization").ShouldBe("Bearer " + SigningKey.Create(Key)!.BearerToken);
        request.Header("X-Inngest-Env").ShouldBe("branch-1");
        request.Header("X-Inngest-SDK").ShouldStartWith("inngest-dotnet:v");
    }

    [Fact]
    public async Task Reads_the_output_with_the_apps_serializer_options()
    {
        _handler.Respond(HttpStatusCode.OK, """{"data":{"run_id":"01J","status":"Completed","function_id":"f","run_started_at":"2026-10-28T18:19:58Z","output":{"greeting_text":"hi"}}}""");
        var client = Create(TestSettings.Create(options =>
            options.SerializerOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web) { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower }));

        var run = await client.GetRunAsync("01J", Cancellation);

        run.GetOutput<SnakeGreeting>().ShouldBe(new SnakeGreeting("hi"));
    }

    [Fact]
    public async Task Lists_the_runs_of_an_event()
    {
        _handler.Respond(HttpStatusCode.OK, $$$"""
            {"data":[
              {{{CompletedRun}}},
              {"run_id":"01M61VV20X89FZAW14GA30BBBB","run_started_at":"2026-10-28T18:19:58.9Z","function_id":"9c1e","function_version":0,
               "environment_id":"e","event_id":"01M61VV209SMJYG2RA0687EJY1","status":"Failed","ended_at":"2026-10-28T18:20:01Z",
               "output":{"name":"Error","message":"boom","stack":"at x"}},
              {"run_id":"01M61VV20X89FZAW14GA30CCCC","run_started_at":"2026-10-28T18:19:59Z","function_id":"7d2f","function_version":0,
               "environment_id":"e","event_id":"01M61VV209SMJYG2RA0687EJY1","status":"Scheduled","ended_at":null,"output":null}
            ],{{{Metadata}}}}
            """);

        var runs = await Create().GetEventRunsAsync("01M61VV209SMJYG2RA0687EJY1", Cancellation);

        _handler.Single.Uri.ShouldBe(new Uri("http://localhost:8288/v1/events/01M61VV209SMJYG2RA0687EJY1/runs"));
        runs.Select(run => (run.RunId, run.Status)).ShouldBe(
        [
            ("01M61VV20X89FZAW14GA30NNNN", FunctionRunStatus.Completed),
            ("01M61VV20X89FZAW14GA30BBBB", FunctionRunStatus.Failed),
            ("01M61VV20X89FZAW14GA30CCCC", FunctionRunStatus.Scheduled),
        ]);
        runs[1].GetOutput<RunError>().ShouldBe(new RunError("Error", "boom"));
        runs[2].EndedAt.ShouldBeNull();
        runs[2].Output.ValueKind.ShouldBe(JsonValueKind.Null);
        runs[2].GetOutput<Hello>().ShouldBeNull();
    }

    [Fact]
    public async Task An_event_without_runs_has_an_empty_list()
    {
        _handler.Respond(HttpStatusCode.OK, $$"""{"data":[],{{Metadata}}}""");

        var runs = await Create().GetEventRunsAsync("01M61VV209SMJYG2RA0687EJY1", Cancellation);

        runs.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(HttpStatusCode.OK)]
    [InlineData(HttpStatusCode.NoContent)]
    public async Task Cancels_a_run_with_a_delete(HttpStatusCode status)
    {
        _handler.Respond(status);

        await Create().CancelRunAsync("01M61VV20X89FZAW14GA30NNNN", Cancellation);

        _handler.Single.Method.ShouldBe(HttpMethod.Delete);
        _handler.Single.Uri.ShouldBe(new Uri("http://localhost:8288/v1/runs/01M61VV20X89FZAW14GA30NNNN"));
    }

    [Fact]
    public async Task Fails_with_the_reason_and_status_inngest_gives()
    {
        // The dev server answers an unknown run with a 500, and a cancel of one with a 404.
        _handler
            .Respond(HttpStatusCode.InternalServerError, """{"error":"Unable to load function run: 01JZZZ","data":null,"status":500}""")
            .Respond(HttpStatusCode.NotFound, """{"error":"Unable to load function run: 01JZZZ","data":null,"status":404}""")
            .Respond(HttpStatusCode.BadRequest, "Invalid run ID", "text/plain");
        var client = Create();

        var get = await Should.ThrowAsync<InngestException>(client.GetRunAsync("01JZZZ", Cancellation));
        var cancel = await Should.ThrowAsync<InngestException>(client.CancelRunAsync("01JZZZ", Cancellation));
        var list = await Should.ThrowAsync<InngestException>(client.GetEventRunsAsync("not-an-id", Cancellation));

        get.Message.ShouldBe("Failed to get run 01JZZZ: Unable to load function run: 01JZZZ");
        get.StatusCode.ShouldBe(500);
        cancel.Message.ShouldBe("Failed to cancel run 01JZZZ: Unable to load function run: 01JZZZ");
        cancel.StatusCode.ShouldBe(404);
        list.Message.ShouldBe("Failed to list the runs of event not-an-id: Invalid run ID");
        list.StatusCode.ShouldBe(400);
    }

    [Fact]
    public async Task Turns_network_failures_broken_bodies_and_timeouts_into_inngest_exceptions()
    {
        _handler
            .Fail(new HttpRequestException("Connection refused"))
            .Respond(HttpStatusCode.OK, """{"data":{"run_id":""")
            .RespondWithTruncatedBody(HttpStatusCode.OK, """{"data":[""")
            .Fail(new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout."))
            .Respond(HttpStatusCode.OK, """{"data":null}""");
        var client = Create();

        var refused = await Should.ThrowAsync<InngestException>(client.GetRunAsync("01J", Cancellation));
        var invalid = await Should.ThrowAsync<InngestException>(client.GetRunAsync("01J", Cancellation));
        var truncated = await Should.ThrowAsync<InngestException>(client.GetEventRunsAsync("01E", Cancellation));
        var timedOut = await Should.ThrowAsync<InngestException>(client.CancelRunAsync("01J", Cancellation));
        var empty = await Should.ThrowAsync<InngestException>(client.GetRunAsync("01J", Cancellation));

        refused.Message.ShouldBe("Failed to get run 01J: Connection refused");
        refused.InnerException.ShouldBeOfType<HttpRequestException>();
        invalid.Message.ShouldBe("Failed to get run 01J: Inngest returned invalid JSON.");
        truncated.InnerException.ShouldBeAssignableTo<IOException>();
        timedOut.Message.ShouldBe("Failed to cancel run 01J: the request to Inngest timed out.");
        empty.Message.ShouldBe("Failed to get run 01J: Inngest's reply has no run.");
    }

    [Fact]
    public async Task Lets_the_callers_cancellation_through()
    {
        _handler.Hang();
        using var cancellation = new CancellationTokenSource();

        var get = Create().GetRunAsync("01J", cancellation.Token);
        await cancellation.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(get);
    }

    [Fact]
    public async Task Reads_a_status_it_does_not_know_as_unknown()
    {
        _handler.Respond(HttpStatusCode.OK, """{"data":{"run_id":"01J","status":"Paused","function_id":"f","run_started_at":"2026-10-28T18:19:58Z"}}""");

        var run = await Create().GetRunAsync("01J", Cancellation);

        run.Status.ShouldBe(FunctionRunStatus.Unknown);
        run.Output.ValueKind.ShouldBe(JsonValueKind.Undefined);
        run.GetOutput<Hello>().ShouldBeNull();
    }

    [Fact]
    public async Task Escapes_ids_in_the_path()
    {
        _handler.Respond(HttpStatusCode.OK, """{"data":[]}""");

        await Create().GetEventRunsAsync("a/b?c", Cancellation);

        _handler.Single.Uri.AbsolutePath.ShouldBe("/v1/events/a%2Fb%3Fc/runs");
    }

    [Fact]
    public async Task Rejects_empty_ids()
    {
        var client = Create();

        await Should.ThrowAsync<ArgumentException>(client.GetRunAsync(string.Empty, Cancellation));
        await Should.ThrowAsync<ArgumentException>(client.GetEventRunsAsync(string.Empty, Cancellation));
        await Should.ThrowAsync<ArgumentException>(client.CancelRunAsync(string.Empty, Cancellation));
        _handler.Requests.ShouldBeEmpty();
    }

    [Fact]
    public void A_run_made_outside_the_client_reads_its_output_with_web_defaults()
    {
        var run = new FunctionRun
        {
            RunId = "01J",
            Status = FunctionRunStatus.Completed,
            FunctionId = "f",
            Output = JsonSerializer.SerializeToElement(new { greeting = "hi" }),
        };

        run.GetOutput<Hello>().ShouldBe(new Hello("hi"));
    }

    [Fact]
    public void Is_registered_by_add_inngest()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddLogging();
        services.AddInngest(options => options.AppId = "shop");
        using var provider = services.BuildServiceProvider();

        var client = provider.GetRequiredService<IInngestRunsClient>();

        client.ShouldBeOfType<InngestRunsClient>();
        provider.GetRequiredService<IInngestRunsClient>().ShouldBeSameAs(client);
    }

    private InngestRunsClient Create(InngestSettings? settings = null) =>
        new(new FakeHttpClientFactory(_handler), settings ?? TestSettings.Create(), new FakeLogger<InngestRunsClient>());

    private sealed record Hello(string Greeting);

    private sealed record SnakeGreeting(string GreetingText);

    private sealed record RunError(string Name, string Message);
}
