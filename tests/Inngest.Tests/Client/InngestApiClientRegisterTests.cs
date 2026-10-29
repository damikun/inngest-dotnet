using System.Net;
using System.Text;
using Inngest.Client;
using Inngest.Internal;
using Inngest.Signing;
using Microsoft.Extensions.Logging;

namespace Inngest.Tests.Client;

public sealed class InngestApiClientRegisterTests
{
    private const string PrimaryKey = "signkey-prod-12345678";
    private const string SigningKeyToken = "Bearer signkey-prod-b2ed992186a5cb19f6668aade821f502c1d00970dfd0e35128d51bac4649916c";
    private const string FallbackKey = "signkey-prod-abcdef01";

    private static readonly byte[] Payload = """{"url":"http://localhost:5000/api/inngest","appName":"shop","functions":[]}"""u8.ToArray();

    private const string Synced = """{"ok":true,"modified":true,"sync_id":"92b7116f-6004-400b-bede-ecb754c4c655","app_id":"34ee71cf-d26b-5925-a750-eb95b9daf65f"}""";

    private static Task<RegisterResult> Register(ApiFixture fixture, string? deployId = null, string? serverKind = null, CancellationToken cancellationToken = default) =>
        fixture.Api.RegisterAsync(Payload, deployId, serverKind, cancellationToken);

    [Fact]
    public async Task Posts_the_payload_to_the_register_endpoint_of_the_dev_server()
    {
        var fixture = new ApiFixture();
        fixture.Handler.Respond(HttpStatusCode.OK, Synced, "text/plain");

        var result = await Register(fixture);

        result.ShouldBe(new RegisterResult(true, 200, true, null));
        var request = fixture.Handler.Single;
        request.Method.ShouldBe(HttpMethod.Post);
        request.Uri.AbsoluteUri.ShouldBe("http://localhost:8288/fn/register");
        request.Body.ShouldBe(Payload);
    }

    [Fact]
    public async Task Sends_the_headers_of_an_out_of_band_sync()
    {
        var fixture = new ApiFixture();
        fixture.Handler.Respond(HttpStatusCode.OK, Synced);

        await Register(fixture);

        var request = fixture.Handler.Single;
        request.Header("Content-Type").ShouldBe("application/json");
        request.Header("X-Inngest-SDK").ShouldBe(SdkInfo.Header);
        request.Header("User-Agent").ShouldBe(SdkInfo.Header);
        request.Header("X-Inngest-Sync-Kind").ShouldBe("out_of_band");
        request.HeaderNames.ShouldNotContain("Authorization");
        request.HeaderNames.ShouldNotContain("X-Inngest-Expected-Server-Kind");
        request.HeaderNames.ShouldNotContain("X-Inngest-Env");
    }

    [Fact]
    public async Task Uses_the_named_inngest_http_client()
    {
        var fixture = new ApiFixture();
        fixture.Handler.Respond(HttpStatusCode.OK, Synced);

        await Register(fixture);

        fixture.Factory.RequestedNames.ShouldBe(["Inngest"]);
    }

    [Fact]
    public async Task Passes_the_deploy_id_in_the_query_string()
    {
        var fixture = new ApiFixture();
        fixture.Handler.Respond(HttpStatusCode.OK, Synced);

        await Register(fixture, deployId: "deploy 1&x=y");

        fixture.Handler.Single.Uri.AbsoluteUri.ShouldBe("http://localhost:8288/fn/register?deployId=deploy%201%26x%3Dy");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task Leaves_out_a_missing_deploy_id(string? deployId)
    {
        var fixture = new ApiFixture();
        fixture.Handler.Respond(HttpStatusCode.OK, Synced);

        await Register(fixture, deployId);

        fixture.Handler.Single.Uri.Query.ShouldBeEmpty();
    }

    [Fact]
    public async Task Copies_the_server_kind_the_sync_expects()
    {
        var fixture = new ApiFixture();
        fixture.Handler.Respond(HttpStatusCode.OK, Synced);

        await Register(fixture, serverKind: "dev");

        fixture.Handler.Single.Header("X-Inngest-Expected-Server-Kind").ShouldBe("dev");
    }

    [Fact]
    public async Task Sends_the_environment()
    {
        var fixture = new ApiFixture(TestSettings.Create(options => options.Environment = "feature-x"));
        fixture.Handler.Respond(HttpStatusCode.OK, Synced);

        await Register(fixture);

        fixture.Handler.Single.Header("X-Inngest-Env").ShouldBe("feature-x");
    }

    [Fact]
    public async Task Authenticates_with_the_hashed_signing_key()
    {
        var fixture = new ApiFixture(TestSettings.Cloud(options => options.SigningKey = PrimaryKey));
        fixture.Handler.Respond(HttpStatusCode.OK, Synced);

        await Register(fixture);

        var request = fixture.Handler.Single;
        request.Uri.AbsoluteUri.ShouldBe("https://api.inngest.com/fn/register");
        request.Header("Authorization").ShouldBe(SigningKeyToken);
        request.BodyText.ShouldNotContain("12345678");
    }

    [Fact]
    public async Task Authenticates_in_dev_mode_when_a_key_is_configured()
    {
        var fixture = new ApiFixture(TestSettings.Create(options => options.SigningKey = PrimaryKey));
        fixture.Handler.Respond(HttpStatusCode.OK, Synced);

        await Register(fixture);

        fixture.Handler.Single.Header("Authorization").ShouldBe(SigningKeyToken);
    }

    [Fact]
    public async Task Resolves_the_path_from_the_root_of_the_api_address()
    {
        var fixture = new ApiFixture(TestSettings.Create(options => options.ApiBaseUrl = new Uri("https://gateway.example.com/inngest/v1/")));
        fixture.Handler.Respond(HttpStatusCode.OK, Synced);

        await Register(fixture, deployId: "d1");

        fixture.Handler.Single.Uri.AbsoluteUri.ShouldBe("https://gateway.example.com/fn/register?deployId=d1");
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task Repeats_the_request_once_with_the_fallback_key_when_the_signing_key_is_rejected(HttpStatusCode rejection)
    {
        var fixture = new ApiFixture(TestSettings.Cloud(options =>
        {
            options.SigningKey = PrimaryKey;
            options.SigningKeyFallback = FallbackKey;
        }));
        fixture.Handler.Respond(rejection, """{"error":"Authentication failed"}""").Respond(HttpStatusCode.OK, Synced);

        var result = await Register(fixture, deployId: "d1", serverKind: "cloud");

        result.ShouldBe(new RegisterResult(true, 200, true, null));
        var requests = fixture.Handler.Requests;
        requests.Count.ShouldBe(2);
        requests[0].Header("Authorization").ShouldBe(SigningKeyToken);
        requests[1].Header("Authorization").ShouldBe("Bearer " + SigningKey.Create(FallbackKey)!.BearerToken);
        requests[1].Header("Authorization").ShouldNotBe(requests[0].Header("Authorization"));
        requests[1].Uri.ShouldBe(requests[0].Uri);
        requests[1].Body.ShouldBe(Payload);
        requests[1].Header("X-Inngest-Sync-Kind").ShouldBe("out_of_band");
        requests[1].Header("X-Inngest-Expected-Server-Kind").ShouldBe("cloud");
    }

    [Fact]
    public async Task Reports_the_failure_when_the_fallback_key_is_rejected_too()
    {
        var fixture = new ApiFixture(TestSettings.Cloud(options =>
        {
            options.SigningKey = PrimaryKey;
            options.SigningKeyFallback = FallbackKey;
        }));
        fixture.Handler.RespondAlways(HttpStatusCode.Unauthorized, "Authentication failed", "text/plain");

        var result = await Register(fixture);

        result.ShouldBe(new RegisterResult(false, 401, false, "Authentication failed"));
        fixture.Handler.Requests.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Does_not_repeat_the_request_when_there_is_no_fallback_key()
    {
        var fixture = new ApiFixture(TestSettings.Cloud(options => options.SigningKey = PrimaryKey));
        fixture.Handler.RespondAlways(HttpStatusCode.Unauthorized, "Authentication failed", "text/plain");

        var result = await Register(fixture);

        result.Success.ShouldBeFalse();
        fixture.Handler.Requests.Count.ShouldBe(1);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task Does_not_use_the_fallback_key_for_other_failures(HttpStatusCode status)
    {
        var fixture = new ApiFixture(TestSettings.Cloud(options =>
        {
            options.SigningKey = PrimaryKey;
            options.SigningKeyFallback = FallbackKey;
        }));
        fixture.Handler.RespondAlways(status, "{}");

        await Register(fixture);

        fixture.Handler.Requests.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Uses_the_fallback_key_alone_when_it_is_all_that_is_configured()
    {
        var fixture = new ApiFixture(TestSettings.Cloud(options => options.SigningKeyFallback = FallbackKey));
        fixture.Handler.Respond(HttpStatusCode.Unauthorized).Respond(HttpStatusCode.OK, Synced);

        var result = await Register(fixture);

        result.Success.ShouldBeTrue();
        fixture.Handler.Requests[0].HeaderNames.ShouldNotContain("Authorization");
        fixture.Handler.Requests[1].Header("Authorization").ShouldNotBeNull();
    }

    [Theory]
    [InlineData("""{"ok":true,"modified":true}""", true)]
    [InlineData("""{"ok":true,"modified":false}""", false)]
    [InlineData("""{"ok":true,"sync_id":"s","app_id":"a"}""", false)]
    [InlineData("""{"ok":true,"modified":null}""", false)]
    [InlineData("OK", false)]
    [InlineData("", false)]
    public async Task Reads_whether_the_sync_changed_anything(string body, bool modified)
    {
        var fixture = new ApiFixture();
        fixture.Handler.Respond(HttpStatusCode.OK, body, "text/plain");

        var result = await Register(fixture);

        result.ShouldBe(new RegisterResult(true, 200, modified, null));
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, """{"error":"App ID required","data":null,"status":400}""", "App ID required")]
    [InlineData(HttpStatusCode.BadRequest, """{"error":"Expected server kind dev, got cloud"}""", "Expected server kind dev, got cloud")]
    [InlineData(HttpStatusCode.BadRequest, """{"code":"sdk_version_denied","error":"App sync was blocked","data":null,"status":400}""", "App sync was blocked")]
    [InlineData(HttpStatusCode.Unauthorized, "Authentication failed", "Authentication failed")]
    [InlineData(HttpStatusCode.InternalServerError, "", "Inngest answered with HTTP 500.")]
    [InlineData(HttpStatusCode.Conflict, """{"error":""}""", """{"error":""}""")]
    [InlineData(HttpStatusCode.BadGateway, """{"error":{"code":"upstream"}}""", """{"error":{"code":"upstream"}}""")]
    [InlineData(HttpStatusCode.Created, "", "Inngest answered with HTTP 201.")]
    [InlineData(HttpStatusCode.NoContent, "", "Inngest answered with HTTP 204.")]
    public async Task Reports_why_inngest_rejected_the_sync(HttpStatusCode status, string body, string expectedError)
    {
        var fixture = new ApiFixture();
        fixture.Handler.Respond(status, body, "text/plain");

        var result = await Register(fixture);

        result.ShouldBe(new RegisterResult(false, (int)status, false, expectedError));
    }

    [Fact]
    public async Task Shortens_the_text_of_a_gateway_error_page()
    {
        var fixture = new ApiFixture();
        fixture.Handler.Respond(HttpStatusCode.BadGateway, "<html>" + new string('x', 5000) + "</html>", "text/html");

        var result = await Register(fixture);

        result.Success.ShouldBeFalse();
        result.Error!.Length.ShouldBeLessThan(400);
        result.Error.ShouldEndWith("...");
    }

    [Fact]
    public async Task Leaves_logging_a_failed_sync_to_the_caller()
    {
        var fixture = new ApiFixture();
        fixture.Handler.Respond(HttpStatusCode.BadRequest, """{"error":"App ID required"}""");

        var result = await Register(fixture);

        result.Error.ShouldBe("App ID required");
        fixture.Logger.Collector.GetSnapshot().ShouldBeEmpty();
    }

    [Fact]
    public async Task Does_not_log_the_signing_key_or_the_token()
    {
        var fixture = new ApiFixture(TestSettings.Cloud(options =>
        {
            options.SigningKey = PrimaryKey;
            options.SigningKeyFallback = FallbackKey;
        }));
        fixture.Handler.RespondAlways(HttpStatusCode.Unauthorized, "Authentication failed", "text/plain");

        await Register(fixture);

        var logged = string.Join('\n', fixture.Logger.Collector.GetSnapshot().Select(record => record.Message));
        logged.ShouldNotContain("12345678");
        logged.ShouldNotContain("b2ed9921");
        logged.ShouldNotContain("abcdef01");
        fixture.Logger.Collector.GetSnapshot().ShouldContain(record => record.Id.Id == 312 && record.Level == LogLevel.Debug);
    }

    [Fact]
    public async Task Reports_an_unreachable_server_as_a_failed_sync()
    {
        var fixture = new ApiFixture();
        fixture.Handler.Fail(new HttpRequestException("Connection refused"));

        var result = await Register(fixture);

        result.ShouldBe(new RegisterResult(false, 500, false, "Failed to register; Connection refused"));
    }

    [Fact]
    public async Task Reports_a_timeout_as_a_failed_sync()
    {
        var fixture = new ApiFixture();
        fixture.Handler.Fail(new TaskCanceledException("The request timed out.", new TimeoutException()));

        var result = await Register(fixture);

        result.Success.ShouldBeFalse();
        result.StatusCode.ShouldBe(500);
        result.Error.ShouldBe("Failed to register; the request to Inngest timed out.");
    }

    [Fact]
    public async Task Does_not_report_a_cancelled_sync_as_failed()
    {
        var fixture = new ApiFixture();
        using var cancellation = new CancellationTokenSource();
        fixture.Handler.Hang();

        var register = Register(fixture, cancellationToken: cancellation.Token);
        while (fixture.Handler.Requests.Count == 0)
        {
            await Task.Delay(1);
        }

        await cancellation.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(register);
    }

    [Fact]
    public async Task Sends_the_payload_unchanged_whatever_its_content()
    {
        var fixture = new ApiFixture();
        fixture.Handler.Respond(HttpStatusCode.OK, Synced);
        var payload = Encoding.UTF8.GetBytes("""{"appName":"Příliš žluťoučký kůň","emoji":"😀"}""");

        await fixture.Api.RegisterAsync(payload, null, null, CancellationToken.None);

        fixture.Handler.Single.Body.ShouldBe(payload);
    }
}
