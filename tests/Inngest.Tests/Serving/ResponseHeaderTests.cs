using Inngest.Internal;
using Inngest.Tests.Serving.Fakes;
using Microsoft.Extensions.Logging;

namespace Inngest.Tests.Serving;

public sealed class ResponseHeaderTests
{
    private readonly CommHandlerHarness _harness = new();

    [Theory]
    [InlineData("GET")]
    [InlineData("PUT")]
    [InlineData("POST")]
    [InlineData("DELETE")]
    public async Task Every_dev_response_identifies_the_sdk(string method)
    {
        var request = new FakeServeRequest(method, "http://localhost:3000/api/inngest?fnId=shop-send-email&stepId=step", "{}");

        using var response = await _harness.SendAsync(CommHandlerHarness.DevSettings, request);

        response.Header("Content-Type").ShouldBe("application/json");
        response.Header("X-Inngest-SDK-Handled").ShouldBe("true");
        response.Header("X-Inngest-SDK").ShouldBe(SdkInfo.Header);
        response.Header("X-Inngest-Req-Version").ShouldBe("2");
        response.Header("X-Inngest-Framework").ShouldBe("test");
        response.Header("X-Inngest-Env").ShouldBeNull();
        response.Header("X-Inngest-Signature").ShouldBeNull();
    }

    [Fact]
    public async Task A_dev_GET_carries_only_the_common_headers()
    {
        using var response = await _harness.SendAsync(CommHandlerHarness.DevSettings, new FakeServeRequest("GET"));

        response.HeaderNames().ShouldBe(
            ["Content-Type", "X-Inngest-SDK-Handled", "X-Inngest-SDK", "X-Inngest-Req-Version", "X-Inngest-Framework"],
            ignoreOrder: true);
    }

    [Fact]
    public async Task Responses_name_the_environment_when_one_is_set()
    {
        var settings = CommHandlerHarness.DevSettings with { Environment = "branch-x" };

        using var response = await _harness.SendAsync(settings, new FakeServeRequest("GET"));

        response.Header("X-Inngest-Env").ShouldBe("branch-x");
    }

    [Fact]
    public async Task Responses_report_the_framework_of_the_request()
    {
        using var response = await _harness.SendAsync(CommHandlerHarness.DevSettings, new FakeServeRequest("GET") { Framework = "aspnetcore" });

        response.Header("X-Inngest-Framework").ShouldBe("aspnetcore");
        response.BodyText().ShouldNotContain("framework");
    }

    [Fact]
    public async Task Trace_context_is_forwarded()
    {
        var request = new FakeServeRequest("GET")
            .WithHeader("traceparent", "00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01")
            .WithHeader("tracestate", "congo=t61rcWkgMzE");

        using var response = await _harness.SendAsync(CommHandlerHarness.DevSettings, request);

        response.Header("traceparent").ShouldBe("00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01");
        response.Header("tracestate").ShouldBe("congo=t61rcWkgMzE");
    }

    [Fact]
    public async Task Trace_context_is_forwarded_on_unauthenticated_responses_too()
    {
        var request = new FakeServeRequest("GET").WithHeader("traceparent", "00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01");

        using var response = await _harness.SendAsync(CommHandlerHarness.CloudSettings, request);

        response.StatusCode.ShouldBe(401);
        response.Header("traceparent").ShouldBe("00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01");
    }

    [Theory]
    [InlineData("DELETE")]
    [InlineData("PATCH")]
    [InlineData("HEAD")]
    [InlineData("OPTIONS")]
    public async Task Other_methods_answer_405(string method)
    {
        using var response = await _harness.SendAsync(CommHandlerHarness.DevSettings, new FakeServeRequest(method));

        response.StatusCode.ShouldBe(405);
        response.BodyText().ShouldBe("""{"message":"Method not allowed"}""");
        _harness.Logger.LatestRecord.Level.ShouldBe(LogLevel.Warning);
    }

    [Fact]
    public async Task Methods_are_matched_without_regard_to_case()
    {
        using var response = await _harness.SendAsync(CommHandlerHarness.DevSettings, new FakeServeRequest("get"));

        response.StatusCode.ShouldBe(200);
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("PUT")]
    [InlineData("POST")]
    public async Task Cloud_mode_without_a_signing_key_answers_500_to_everything(string method)
    {
        var settings = CommHandlerHarness.CloudSettings with { SigningKey = null };
        var request = new FakeServeRequest(method, "http://localhost:3000/api/inngest?fnId=shop-send-email&stepId=step", "{}")
            .WithHeader("traceparent", "00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01");

        using var response = await _harness.SendAsync(settings, request);

        response.StatusCode.ShouldBe(500);
        response.BodyText().ShouldBe("""{"code":"internal_server_error"}""");
        response.HeaderNames().ShouldBe(["Content-Type", "X-Inngest-SDK-Handled", "traceparent"], ignoreOrder: true);
        _harness.Engine.Calls.ShouldBeEmpty();
        _harness.Api.Registrations.ShouldBeEmpty();
        _harness.Logger.LatestRecord.Level.ShouldBe(LogLevel.Error);
        _harness.Logger.LatestRecord.Id.Id.ShouldBe(400);
    }

    [Fact]
    public async Task Cloud_mode_without_a_signing_key_fails_even_with_a_fallback_key()
    {
        var settings = CommHandlerHarness.CloudSettings with { SigningKey = null, SigningKeyFallback = CommHandlerHarness.FallbackKey };
        var request = _harness.Sign(new FakeServeRequest("GET"), CommHandlerHarness.FallbackKey);

        using var response = await _harness.SendAsync(settings, request);

        response.StatusCode.ShouldBe(500);
    }
}
