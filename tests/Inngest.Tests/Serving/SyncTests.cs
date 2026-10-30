using Inngest.Client;
using Inngest.Internal;
using Inngest.Tests.Serving.Fakes;
using Microsoft.Extensions.Logging;

namespace Inngest.Tests.Serving;

public sealed class SyncTests
{
    private readonly CommHandlerHarness _harness = new();

    [Fact]
    public async Task An_out_of_band_sync_registers_the_app_with_Inngest()
    {
        var request = new FakeServeRequest("PUT", "http://localhost:3000/api/inngest", """{"url":"http://ignored"}""")
            .WithHeader("X-Inngest-Server-Kind", "dev");

        using var response = await _harness.SendAsync(CommHandlerHarness.DevSettings, request);

        response.StatusCode.ShouldBe(200);
        response.BodyText().ShouldBe("""{"message":"Successfully registered","modified":true}""");
        response.Header("X-Inngest-Sync-Kind").ShouldBe("out_of_band");
        var serveUrl = new Uri("http://localhost:3000/api/inngest");
        _harness.Registry.Registrations.ShouldBe([(serveUrl, "test")]);
        _harness.Api.Registrations.ShouldBe([new FakeInngestApi.Registration(FakeFunctionRegistry.RegistrationJson(serveUrl, "test"), null, "dev")]);
        _harness.Logger.LatestRecord.Level.ShouldBe(LogLevel.Information);
    }

    [Fact]
    public async Task An_unchanged_app_reports_that_nothing_was_modified()
    {
        _harness.Api.Result = new RegisterResult(Success: true, StatusCode: 200, Modified: false, Error: null);

        using var response = await _harness.SendAsync(CommHandlerHarness.DevSettings, new FakeServeRequest("PUT"));

        response.StatusCode.ShouldBe(200);
        response.BodyText().ShouldBe("""{"message":"Successfully registered","modified":false}""");
        _harness.Logger.LatestRecord.Level.ShouldBe(LogLevel.Debug);
    }

    [Fact]
    public async Task A_rejected_sync_answers_500_with_the_error()
    {
        _harness.Api.Result = new RegisterResult(Success: false, StatusCode: 400, Modified: true, Error: "App ID required");

        using var response = await _harness.SendAsync(CommHandlerHarness.DevSettings, new FakeServeRequest("PUT"));

        response.StatusCode.ShouldBe(500);
        response.BodyText().ShouldBe("""{"message":"App ID required","modified":false}""");
        response.Header("X-Inngest-Sync-Kind").ShouldBe("out_of_band");
        _harness.Logger.LatestRecord.Level.ShouldBe(LogLevel.Error);
    }

    [Fact]
    public async Task A_rejected_sync_without_an_error_still_explains_itself()
    {
        _harness.Api.Result = new RegisterResult(Success: false, StatusCode: 502, Modified: false, Error: null);

        using var response = await _harness.SendAsync(CommHandlerHarness.DevSettings, new FakeServeRequest("PUT"));

        response.StatusCode.ShouldBe(500);
        response.BodyText().ShouldBe("""{"message":"Failed to register","modified":false}""");
    }

    [Fact]
    public async Task A_sync_that_cannot_reach_Inngest_answers_500()
    {
        _harness.Api.Failure = new HttpRequestException("Connection refused (localhost:8288)");

        using var response = await _harness.SendAsync(CommHandlerHarness.DevSettings, new FakeServeRequest("PUT"));

        response.StatusCode.ShouldBe(500);
        response.BodyText().ShouldBe("""{"message":"Failed to register; Connection refused (localhost:8288)","modified":false}""");
        _harness.Logger.LatestRecord.Exception.ShouldBeOfType<HttpRequestException>();
    }

    [Fact]
    public async Task A_registration_that_times_out_is_reported_as_a_failure()
    {
        // The registration has no token of the request's, so a cancellation can only be the HTTP client's timeout.
        _harness.Api.Failure = new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout of 100 seconds elapsing.");

        using var response = await _harness.SendAsync(CommHandlerHarness.DevSettings, new FakeServeRequest("PUT"));

        response.StatusCode.ShouldBe(500);
        _harness.Logger.LatestRecord.Level.ShouldBe(LogLevel.Error);
    }

    [Fact]
    public async Task An_out_of_band_sync_whose_configuration_fails_answers_500()
    {
        _harness.Registry.Failure = new InvalidOperationException("Function \"send-email\" has no triggers.");

        using var response = await _harness.SendAsync(CommHandlerHarness.DevSettings, new FakeServeRequest("PUT"));

        response.StatusCode.ShouldBe(500);
        response.BodyText().ShouldBe("""{"message":"Failed to register; Function \"send-email\" has no triggers.","modified":false}""");
        _harness.Api.Registrations.ShouldBeEmpty();
    }

    [Fact]
    public async Task An_in_band_sync_whose_configuration_fails_answers_500()
    {
        _harness.Registry.Failure = new InvalidOperationException("Function \"send-email\" has no triggers.");
        var request = new FakeServeRequest("PUT", body: """{"url":"https://shop.example.com/api/inngest"}""")
            .WithHeader("X-Inngest-Sync-Kind", "in_band");

        using var response = await _harness.SendAsync(CommHandlerHarness.DevSettings, request);

        response.StatusCode.ShouldBe(500);
        response.BodyText().ShouldStartWith("""{"type":"internal","name":"InvalidOperationException","message":"Function \"send-email\" has no triggers.","stack":"System.InvalidOperationException""");
        response.Header("X-Inngest-Sync-Kind").ShouldBeNull();
        _harness.Logger.LatestRecord.Id.Id.ShouldBe(413);
    }

    [Theory]
    [InlineData("?deployId=deploy-1", "deploy-1", "")]
    [InlineData("?deployId=undefined", null, "")]
    [InlineData("?deployId=", null, "")]
    [InlineData("?region=eu&deployId=deploy-1&tier=2", "deploy-1", "?region=eu&tier=2")]
    [InlineData("?DeployId=deploy-1", "deploy-1", "")]
    [InlineData("?region=eu", null, "?region=eu")]
    [InlineData("?deployIdentity=1", null, "?deployIdentity=1")]
    public async Task The_deploy_id_is_forwarded_to_Inngest_and_left_out_of_the_url(string query, string? deployId, string remainingQuery)
    {
        var request = new FakeServeRequest("PUT", "http://localhost:3000/api/inngest" + query);

        using var response = await _harness.SendAsync(CommHandlerHarness.DevSettings, request);

        response.StatusCode.ShouldBe(200);
        _harness.Api.Registrations.ShouldHaveSingleItem().DeployId.ShouldBe(deployId);
        _harness.Registry.Registrations.ShouldHaveSingleItem().ServeUrl.ShouldBe(new Uri("http://localhost:3000/api/inngest" + remainingQuery));
    }

    [Theory]
    [InlineData("http://localhost:3000/api/inngest", null, null, "http://localhost:3000/api/inngest")]
    [InlineData("http://localhost:3000/api/inngest", "https://shop.example.com", null, "https://shop.example.com/api/inngest")]
    [InlineData("http://localhost:3000/api/inngest", null, "/inngest", "http://localhost:3000/inngest")]
    [InlineData("http://localhost:3000/api/inngest", null, "x/inngest", "http://localhost:3000/x/inngest")]
    [InlineData("http://10.0.0.7:8080/api/inngest?region=eu", "https://shop.example.com:8443/ignored/path", "/inngest", "https://shop.example.com:8443/inngest?region=eu")]
    [InlineData("http://10.0.0.7:8080/api/inngest?deployId=d1&region=eu", "https://shop.example.com", null, "https://shop.example.com/api/inngest?region=eu")]
    public async Task The_serve_url_applies_the_serve_path_and_origin_overrides(string requestUrl, string? serveOrigin, string? servePath, string expected)
    {
        var settings = CommHandlerHarness.DevSettings with
        {
            ServeOrigin = serveOrigin is null ? null : new Uri(serveOrigin),
            ServePath = servePath,
        };

        using var response = await _harness.SendAsync(settings, new FakeServeRequest("PUT", requestUrl));

        _harness.Registry.Registrations.ShouldHaveSingleItem().ServeUrl.ShouldBe(new Uri(expected));
        _harness.Api.Registrations.ShouldHaveSingleItem().Payload.ShouldContain($"\"url\":\"{expected}\"");
    }

    [Fact]
    public async Task Cloud_mode_accepts_an_unsigned_out_of_band_sync_but_does_not_identify_itself()
    {
        using var response = await _harness.SendAsync(CommHandlerHarness.CloudSettings, new FakeServeRequest("PUT"));

        response.StatusCode.ShouldBe(200);
        response.BodyText().ShouldBe("""{"message":"Successfully registered","modified":true}""");
        _harness.Api.Registrations.ShouldHaveSingleItem();
        response.HeaderNames().ShouldBe(["Content-Type", "X-Inngest-SDK-Handled"], ignoreOrder: true);
    }

    [Fact]
    public async Task Cloud_mode_can_refuse_unsigned_syncs()
    {
        var settings = CommHandlerHarness.CloudSettings with { AllowUnsignedSync = false };

        using var response = await _harness.SendAsync(settings, new FakeServeRequest("PUT"));

        response.StatusCode.ShouldBe(401);
        _harness.Api.Registrations.ShouldBeEmpty();
    }

    [Fact]
    public async Task An_unsigned_sync_that_takes_the_url_from_the_request_is_warned_about()
    {
        // The Host header is whatever the caller sends.
        using var response = await _harness.SendAsync(CommHandlerHarness.CloudSettings, new FakeServeRequest("PUT", "http://evil.example/api/inngest"));

        response.StatusCode.ShouldBe(200);
        var warning = _harness.Logger.Collector.GetSnapshot().Where(record => record.Id.Id == 415).ShouldHaveSingleItem();
        warning.Level.ShouldBe(LogLevel.Warning);
        warning.Message.ShouldContain("http://evil.example/api/inngest");
    }

    [Fact]
    public async Task An_unsigned_sync_registers_the_serve_origin_without_a_warning()
    {
        var settings = CommHandlerHarness.CloudSettings with { ServeOrigin = new Uri("https://shop.example.com") };

        using var response = await _harness.SendAsync(settings, new FakeServeRequest("PUT", "http://evil.example/api/inngest"));

        response.StatusCode.ShouldBe(200);
        _harness.Api.Registrations.ShouldHaveSingleItem().Payload.ShouldContain("\"url\":\"https://shop.example.com/api/inngest\"");
        _harness.Logger.Collector.GetSnapshot().ShouldNotContain(record => record.Id.Id == 415);
    }

    [Fact]
    public async Task The_registration_goes_on_when_the_caller_hangs_up()
    {
        using var hangUp = new CancellationTokenSource();

        using var response = await _harness.CreateHandler(CommHandlerHarness.DevSettings).HandleAsync(new FakeServeRequest("PUT"), hangUp.Token);

        // A deploy script with a short timeout must not cut the registration off halfway.
        response.StatusCode.ShouldBe(200);
        _harness.Api.RegisterToken.CanBeCanceled.ShouldBeFalse();
    }

    [Fact]
    public async Task An_unsigned_caller_is_not_told_why_a_sync_failed()
    {
        _harness.Api.Result = new RegisterResult(Success: false, StatusCode: 400, Modified: false, Error: "Invalid function config for shop-checkout");

        using var response = await _harness.SendAsync(CommHandlerHarness.CloudSettings, new FakeServeRequest("PUT"));

        response.StatusCode.ShouldBe(500);
        response.BodyText().ShouldBe("""{"message":"Failed to register","modified":false}""");
    }

    [Fact]
    public async Task Cloud_mode_signs_the_answer_to_a_signed_out_of_band_sync()
    {
        var request = _harness.Sign(new FakeServeRequest("PUT", body: """{"url":"http://localhost:3000/api/inngest"}"""), CommHandlerHarness.PrimaryKey);

        using var response = await _harness.SendAsync(CommHandlerHarness.CloudSettings, request);

        response.StatusCode.ShouldBe(200);
        response.Header("X-Inngest-Sync-Kind").ShouldBe("out_of_band");
        response.Header("X-Inngest-SDK").ShouldBe(SdkInfo.Header);
        _harness.IsSignedWith(response, CommHandlerHarness.PrimaryKey).ShouldBeTrue();
    }

    [Fact]
    public async Task An_in_band_sync_answers_with_the_configuration()
    {
        var request = new FakeServeRequest("PUT", "http://10.0.0.7:8080/api/inngest", """{"url":"https://shop.example.com/api/inngest"}""")
            .WithHeader("X-Inngest-Sync-Kind", "in_band");

        using var response = await _harness.SendAsync(CommHandlerHarness.DevSettings, request);

        response.StatusCode.ShouldBe(200);
        response.Header("X-Inngest-Sync-Kind").ShouldBe("in_band");
        response.BodyText().ShouldBe(
            $$"""{"app_id":"shop","capabilities":{"trust_probe":"v1"},"env":null,"framework":"test","functions":{{FakeFunctionRegistry.FunctionsJson}},"inspection":{"authentication_succeeded":null,"capabilities":{"trust_probe":"v1"},"function_count":2,"has_event_key":false,"has_signing_key":false,"has_signing_key_fallback":false,"mode":"dev","schema_version":"2024-05-24"},"sdk_author":"inngest-dotnet","sdk_language":"","sdk_version":"","sdk":"{{SdkInfo.RegistrationValue}}","url":"https://shop.example.com/api/inngest"}""");
        _harness.Registry.Registrations.ShouldBe([(new Uri("https://shop.example.com/api/inngest"), "test")]);
        _harness.Api.Registrations.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_signed_in_band_sync_in_cloud_mode_includes_the_authenticated_introspection()
    {
        var settings = CommHandlerHarness.CloudSettings with { AppVersion = "1.4.0", Environment = "branch-x" };
        var request = _harness.Sign(
            new FakeServeRequest("PUT", body: """{"url":"https://shop.example.com/api/inngest"}""").WithHeader("X-Inngest-Sync-Kind", "in_band"),
            CommHandlerHarness.PrimaryKey);

        using var response = await _harness.SendAsync(settings, request);

        response.StatusCode.ShouldBe(200);
        response.BodyText().ShouldBe(
            $$"""{"app_id":"shop","appVersion":"1.4.0","capabilities":{"trust_probe":"v1"},"env":"branch-x","framework":"test","functions":{{FakeFunctionRegistry.FunctionsJson}},"inspection":{"api_origin":"https://api.inngest.com/","app_id":"shop","authentication_succeeded":true,"capabilities":{"trust_probe":"v1"},"env":"branch-x","event_api_origin":"https://inn.gs/","event_key_hash":null,"extra":{"is_streaming":false},"framework":"test","function_count":2,"has_event_key":false,"has_signing_key":true,"has_signing_key_fallback":false,"mode":"cloud","schema_version":"2024-05-24","sdk_language":"dotnet","sdk_version":"{{SdkInfo.Version}}","serve_origin":null,"serve_path":null,"signing_key_fallback_hash":null,"signing_key_hash":"66687aadf862"},"sdk_author":"inngest-dotnet","sdk_language":"dotnet","sdk_version":"{{SdkInfo.Version}}","sdk":"{{SdkInfo.RegistrationValue}}","url":"https://shop.example.com/api/inngest"}""");
        response.Header("X-Inngest-Sync-Kind").ShouldBe("in_band");
        _harness.IsSignedWith(response, CommHandlerHarness.PrimaryKey).ShouldBeTrue();
    }

    [Fact]
    public async Task An_in_band_sync_applies_the_overrides_to_the_url_from_the_body()
    {
        var settings = CommHandlerHarness.DevSettings with { ServeOrigin = new Uri("https://public.example.com"), ServePath = "/inngest" };
        var request = new FakeServeRequest("PUT", body: """{"url":"http://internal:8080/api/inngest?region=eu&deployId=d1"}""")
            .WithHeader("X-Inngest-Sync-Kind", "in_band");

        using var response = await _harness.SendAsync(settings, request);

        _harness.Registry.Registrations.ShouldHaveSingleItem().ServeUrl.ShouldBe(new Uri("https://public.example.com/inngest?region=eu"));
        response.BodyText().ShouldEndWith("\"url\":\"https://public.example.com/inngest?region=eu\"}");
    }

    [Fact]
    public async Task An_in_band_sync_falls_back_to_out_of_band_when_in_band_syncs_are_disabled()
    {
        var settings = CommHandlerHarness.DevSettings with { AllowInBandSync = false };
        var request = new FakeServeRequest("PUT", body: """{"url":"https://shop.example.com/api/inngest"}""")
            .WithHeader("X-Inngest-Sync-Kind", "in_band");

        using var response = await _harness.SendAsync(settings, request);

        response.Header("X-Inngest-Sync-Kind").ShouldBe("out_of_band");
        _harness.Api.Registrations.ShouldHaveSingleItem();
    }

    [Theory]
    [InlineData("out_of_band")]
    [InlineData("in-band")]
    [InlineData("IN_BAND")]
    public async Task Only_the_exact_in_band_value_asks_for_an_in_band_sync(string syncKind)
    {
        var request = new FakeServeRequest("PUT", body: """{"url":"https://shop.example.com/api/inngest"}""")
            .WithHeader("X-Inngest-Sync-Kind", syncKind);

        using var response = await _harness.SendAsync(CommHandlerHarness.DevSettings, request);

        response.Header("X-Inngest-Sync-Kind").ShouldBe("out_of_band");
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("""{"url":42}""")]
    [InlineData("""{"url":null}""")]
    [InlineData("""{"url":"https://shop.example.com/api/inngest","extra":true}""")]
    [InlineData("""{"url":"/api/inngest"}""")]
    [InlineData("""{"url":"ftp://shop.example.com/api/inngest"}""")]
    public async Task An_in_band_sync_with_an_invalid_body_answers_400(string body)
    {
        var request = new FakeServeRequest("PUT", body: body).WithHeader("X-Inngest-Sync-Kind", "in_band");

        using var response = await _harness.SendAsync(CommHandlerHarness.DevSettings, request);

        response.StatusCode.ShouldBe(400);
        response.BodyText().ShouldStartWith("""{"code":"invalid_request","message":""");
        _harness.Registry.Registrations.ShouldBeEmpty();
        _harness.Api.Registrations.ShouldBeEmpty();
    }

    [Fact]
    public async Task An_in_band_sync_names_the_unrecognized_key()
    {
        var request = new FakeServeRequest("PUT", body: """{"url":"https://shop.example.com/api/inngest","deployId":"d1"}""")
            .WithHeader("X-Inngest-Sync-Kind", "in_band");

        using var response = await _harness.SendAsync(CommHandlerHarness.DevSettings, request);

        response.BodyText().ShouldBe("""{"code":"invalid_request","message":"Unrecognized key \"deployId\"."}""");
    }
}
