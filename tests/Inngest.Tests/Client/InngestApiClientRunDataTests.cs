using System.Net;
using System.Text.Json;
using Inngest.Internal;
using Inngest.Signing;

namespace Inngest.Tests.Client;

public sealed class InngestApiClientRunDataTests
{
    private const string RunId = "01HQ0000000000000000000009";
    private const string PrimaryKey = "signkey-prod-12345678";
    private const string PrimaryToken = "Bearer signkey-prod-b2ed992186a5cb19f6668aade821f502c1d00970dfd0e35128d51bac4649916c";
    private const string FallbackKey = "signkey-prod-abcdef01";

    private const string Steps = """{"e7d8a2f140845095749d60246ff1110c9d01d76a":{"data":"A"},"2c4ac3":{"error":{"name":"Error","message":"boom"}},"8f1a2b":null}""";
    private const string Events = """[{"name":"cart/paid","data":{"total":12},"user":{},"id":"01HQ0000000000000000000001","ts":1705586504000}]""";

    public static TheoryData<string, string> Resources => new()
    {
        { "steps", "actions" },
        { "events", "batch" },
    };

    public static TheoryData<string> Kinds => ["steps", "events"];

    private static Task<JsonDocument> Fetch(ApiFixture fixture, string kind, string runId = RunId, CancellationToken cancellationToken = default) =>
        kind == "steps" ? fixture.Api.GetRunStepsAsync(runId, cancellationToken) : fixture.Api.GetRunEventsAsync(runId, cancellationToken);

    [Fact]
    public async Task Gets_the_steps_of_a_run_from_the_actions_endpoint()
    {
        var fixture = new ApiFixture();
        fixture.Handler.Respond(HttpStatusCode.OK, Steps);

        using var document = await fixture.Api.GetRunStepsAsync(RunId, CancellationToken.None);

        var request = fixture.Handler.Single;
        request.Method.ShouldBe(HttpMethod.Get);
        request.Uri.AbsoluteUri.ShouldBe($"http://localhost:8288/v0/runs/{RunId}/actions");
        request.Body.ShouldBeEmpty();
        document.RootElement.GetProperty("e7d8a2f140845095749d60246ff1110c9d01d76a").GetProperty("data").GetString().ShouldBe("A");
        document.RootElement.GetProperty("2c4ac3").GetProperty("error").GetProperty("message").GetString().ShouldBe("boom");
        document.RootElement.GetProperty("8f1a2b").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task Gets_the_events_of_a_run_from_the_batch_endpoint()
    {
        var fixture = new ApiFixture();
        fixture.Handler.Respond(HttpStatusCode.OK, Events);

        using var document = await fixture.Api.GetRunEventsAsync(RunId, CancellationToken.None);

        fixture.Handler.Single.Uri.AbsoluteUri.ShouldBe($"http://localhost:8288/v0/runs/{RunId}/batch");
        document.RootElement.GetArrayLength().ShouldBe(1);
        document.RootElement[0].GetProperty("name").GetString().ShouldBe("cart/paid");
    }

    [Theory]
    [MemberData(nameof(Resources))]
    public async Task Sends_the_sdk_headers_and_the_environment(string kind, string resource)
    {
        var fixture = new ApiFixture(TestSettings.Create(options => options.Environment = "feature-x"));
        fixture.Handler.Respond(HttpStatusCode.OK, "{}");

        using var document = await Fetch(fixture, kind);

        var request = fixture.Handler.Single;
        request.Uri.AbsolutePath.ShouldEndWith("/" + resource);
        request.Header("X-Inngest-SDK").ShouldBe(SdkInfo.Header);
        request.Header("User-Agent").ShouldBe(SdkInfo.Header);
        request.Header("X-Inngest-Env").ShouldBe("feature-x");
        request.HeaderNames.ShouldNotContain("Authorization");
        request.HeaderNames.ShouldNotContain("X-Inngest-Sync-Kind");
        request.HeaderNames.ShouldNotContain("Content-Type");
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task Uses_the_named_inngest_http_client(string kind)
    {
        var fixture = new ApiFixture();
        fixture.Handler.Respond(HttpStatusCode.OK, "{}");

        using var document = await Fetch(fixture, kind);

        fixture.Factory.RequestedNames.ShouldBe(["Inngest"]);
    }

    [Theory]
    [MemberData(nameof(Resources))]
    public async Task Authenticates_with_the_hashed_signing_key(string kind, string resource)
    {
        var fixture = new ApiFixture(TestSettings.Cloud(options => options.SigningKey = PrimaryKey));
        fixture.Handler.Respond(HttpStatusCode.OK, "{}");

        using var document = await Fetch(fixture, kind);

        var request = fixture.Handler.Single;
        request.Uri.AbsoluteUri.ShouldBe($"https://api.inngest.com/v0/runs/{RunId}/{resource}");
        request.Header("Authorization").ShouldBe(PrimaryToken);
    }

    [Theory]
    [InlineData("steps", HttpStatusCode.Unauthorized)]
    [InlineData("steps", HttpStatusCode.Forbidden)]
    [InlineData("events", HttpStatusCode.Unauthorized)]
    [InlineData("events", HttpStatusCode.Forbidden)]
    public async Task Repeats_the_request_once_with_the_fallback_key_when_the_signing_key_is_rejected(string kind, HttpStatusCode rejection)
    {
        var fixture = new ApiFixture(TestSettings.Cloud(options =>
        {
            options.SigningKey = PrimaryKey;
            options.SigningKeyFallback = FallbackKey;
        }));
        fixture.Handler.Respond(rejection, """{"error":"Authentication failed","status":401}""").Respond(HttpStatusCode.OK, kind == "steps" ? Steps : Events);

        using var document = await Fetch(fixture, kind);

        var requests = fixture.Handler.Requests;
        requests.Count.ShouldBe(2);
        requests[0].Header("Authorization").ShouldBe(PrimaryToken);
        requests[1].Header("Authorization").ShouldBe("Bearer " + SigningKey.Create(FallbackKey)!.BearerToken);
        requests[1].Uri.ShouldBe(requests[0].Uri);
        document.RootElement.ValueKind.ShouldBe(kind == "steps" ? JsonValueKind.Object : JsonValueKind.Array);
    }

    [Fact]
    public async Task Fails_when_the_fallback_key_is_rejected_too()
    {
        var fixture = new ApiFixture(TestSettings.Cloud(options =>
        {
            options.SigningKey = PrimaryKey;
            options.SigningKeyFallback = FallbackKey;
        }));
        fixture.Handler.RespondAlways(HttpStatusCode.Unauthorized, """{"error":"Authentication failed","status":401}""");

        var exception = await Should.ThrowAsync<InngestException>(fixture.Api.GetRunStepsAsync(RunId, CancellationToken.None));

        exception.StatusCode.ShouldBe(401);
        fixture.Handler.Requests.Count.ShouldBe(2);
    }

    [Theory]
    [MemberData(nameof(Resources))]
    public async Task Escapes_the_run_id_and_resolves_from_the_root_of_the_api_address(string kind, string resource)
    {
        var fixture = new ApiFixture(TestSettings.Create(options => options.ApiBaseUrl = new Uri("https://gateway.example.com/inngest/")));
        fixture.Handler.Respond(HttpStatusCode.OK, "{}");

        using var document = await Fetch(fixture, kind, "run/1 ?x");

        fixture.Handler.Single.Uri.AbsoluteUri.ShouldBe($"https://gateway.example.com/v0/runs/run%2F1%20%3Fx/{resource}");
    }

    [Theory]
    [InlineData("steps", HttpStatusCode.NotFound, """{"error":"run not found","status":404}""", "Failed to retrieve the steps of run 01HQ0000000000000000000009: run not found")]
    [InlineData("events", HttpStatusCode.NotFound, """{"error":"run not found","status":404}""", "Failed to retrieve the events of run 01HQ0000000000000000000009: run not found")]
    [InlineData("steps", HttpStatusCode.InternalServerError, "", "Failed to retrieve the steps of run 01HQ0000000000000000000009: Inngest answered with HTTP 500.")]
    [InlineData("events", HttpStatusCode.BadGateway, "upstream down", "Failed to retrieve the events of run 01HQ0000000000000000000009: upstream down")]
    [InlineData("steps", HttpStatusCode.NoContent, "", "Failed to retrieve the steps of run 01HQ0000000000000000000009: Inngest answered with HTTP 204.")]
    public async Task Throws_with_the_error_of_the_server_when_the_answer_is_not_200(string kind, HttpStatusCode status, string body, string expectedMessage)
    {
        var fixture = new ApiFixture();
        fixture.Handler.Respond(status, body);

        var exception = await Should.ThrowAsync<InngestException>(Fetch(fixture, kind));

        exception.StatusCode.ShouldBe((int)status);
        exception.Message.ShouldBe(expectedMessage);
        fixture.Handler.Requests.Count.ShouldBe(1);
    }

    [Theory]
    [InlineData("steps", "")]
    [InlineData("steps", "not json")]
    [InlineData("events", """[{"name":""")]
    public async Task Throws_when_a_successful_answer_is_not_json(string kind, string body)
    {
        var fixture = new ApiFixture();
        fixture.Handler.Respond(HttpStatusCode.OK, body);

        var exception = await Should.ThrowAsync<InngestException>(Fetch(fixture, kind));

        exception.Message.ShouldContain("invalid JSON");
        exception.InnerException.ShouldBeAssignableTo<JsonException>();
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task Throws_when_the_answer_is_cut_short(string kind)
    {
        var fixture = new ApiFixture();
        fixture.Handler.RespondWithTruncatedBody(HttpStatusCode.OK, kind == "steps" ? """{"abc":{"data":""" : """[{"name":""");

        var exception = await Should.ThrowAsync<InngestException>(Fetch(fixture, kind));

        exception.InnerException.ShouldBeAssignableTo<IOException>();
        exception.Message.ShouldContain(RunId);
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task Throws_when_the_server_cannot_be_reached(string kind)
    {
        var fixture = new ApiFixture();
        var failure = new HttpRequestException("Connection refused");
        fixture.Handler.Fail(failure);

        var exception = await Should.ThrowAsync<InngestException>(Fetch(fixture, kind));

        exception.InnerException.ShouldBeSameAs(failure);
        exception.Message.ShouldContain("Connection refused");
        exception.Message.ShouldContain(RunId);
    }

    [Fact]
    public async Task Throws_when_the_request_times_out()
    {
        var fixture = new ApiFixture();
        fixture.Handler.Fail(new TaskCanceledException("The request timed out.", new TimeoutException()));

        var exception = await Should.ThrowAsync<InngestException>(fixture.Api.GetRunEventsAsync(RunId, CancellationToken.None));

        exception.Message.ShouldContain("timed out");
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task Honours_cancellation(string kind)
    {
        var fixture = new ApiFixture();
        using var cancellation = new CancellationTokenSource();
        fixture.Handler.Hang();

        var fetch = Fetch(fixture, kind, cancellationToken: cancellation.Token);
        while (fixture.Handler.Requests.Count == 0)
        {
            await Task.Delay(1);
        }

        await cancellation.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(fetch);
    }

    [Theory]
    [InlineData("steps", null)]
    [InlineData("steps", "")]
    [InlineData("events", null)]
    [InlineData("events", "")]
    public async Task Requires_a_run_id(string kind, string? runId)
    {
        var fixture = new ApiFixture();

        await Should.ThrowAsync<ArgumentException>(Fetch(fixture, kind, runId!));

        fixture.Handler.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task Parses_a_large_answer()
    {
        var fixture = new ApiFixture();
        var steps = string.Join(",", Enumerable.Range(0, 20_000).Select(i => $"\"step-{i}\":{{\"data\":\"{new string('x', 60)}\"}}"));
        fixture.Handler.Respond(HttpStatusCode.OK, "{" + steps + "}");

        using var document = await fixture.Api.GetRunStepsAsync(RunId, CancellationToken.None);

        document.RootElement.EnumerateObject().Count().ShouldBe(20_000);
    }
}
