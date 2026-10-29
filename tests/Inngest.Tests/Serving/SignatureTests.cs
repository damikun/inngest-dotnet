using Inngest.Serving;
using Inngest.Tests.Serving.Fakes;
using Microsoft.Extensions.Logging;

namespace Inngest.Tests.Serving;

// Cloud mode: which requests must be signed, what an unauthenticated caller sees, and how responses are signed.
public sealed class SignatureTests
{
    private const string CallUrl = "http://localhost:3000/api/inngest?fnId=shop-send-email&stepId=step";
    private const string CallBody = """{"ctx":{"run_id":"01J0000000000000000000000"},"event":{"name":"user/signed.up"},"steps":{},"version":2}""";
    private const string InBandBody = """{"url":"https://shop.example.com/api/inngest"}""";

    private readonly CommHandlerHarness _harness = new();

    public static TheoryData<string> GuardedRequests => ["GET", "POST", "PUT in-band"];

    [Theory]
    [MemberData(nameof(GuardedRequests))]
    public async Task A_missing_signature_is_rejected(string kind)
    {
        using var response = await _harness.SendAsync(CommHandlerHarness.CloudSettings, Request(kind));

        ShouldBeUnauthorized(response);

        // Health probes and scanners send no signature; only a wrong signature is worth a warning.
        _harness.Logger.LatestRecord.Level.ShouldBe(LogLevel.Debug);
        _harness.Logger.LatestRecord.Message.ShouldContain("unsigned");
    }

    [Theory]
    [MemberData(nameof(GuardedRequests))]
    public async Task An_expired_signature_is_rejected(string kind)
    {
        var request = _harness.Sign(Request(kind), CommHandlerHarness.PrimaryKey);
        _harness.Time.Advance(TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(1));

        using var response = await _harness.SendAsync(CommHandlerHarness.CloudSettings, request);

        ShouldBeUnauthorized(response);
        _harness.Logger.LatestRecord.Message.ShouldContain("expired");
    }

    [Theory]
    [MemberData(nameof(GuardedRequests))]
    public async Task A_signature_from_another_key_is_rejected(string kind)
    {
        var settings = CommHandlerHarness.CloudSettings with { SigningKeyFallback = CommHandlerHarness.FallbackKey };
        var request = _harness.Sign(Request(kind), CommHandlerHarness.UnknownKey);

        using var response = await _harness.SendAsync(settings, request);

        ShouldBeUnauthorized(response);
    }

    [Theory]
    [InlineData("t=1793275200")]
    [InlineData("s=0000")]
    [InlineData("garbage")]
    public async Task A_malformed_signature_is_rejected(string signature)
    {
        var request = new FakeServeRequest("POST", CallUrl, CallBody).WithHeader("X-Inngest-Signature", signature);

        using var response = await _harness.SendAsync(CommHandlerHarness.CloudSettings, request);

        ShouldBeUnauthorized(response);
        _harness.Engine.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_signature_over_a_different_body_is_rejected()
    {
        var signed = _harness.Sign(new FakeServeRequest("POST", CallUrl, CallBody), CommHandlerHarness.PrimaryKey);
        var tampered = new FakeServeRequest("POST", CallUrl, CallBody.Replace("user/signed.up", "user/deleted", StringComparison.Ordinal))
            .WithHeader("X-Inngest-Signature", signed.RequestHeaders["X-Inngest-Signature"]);

        using var response = await _harness.SendAsync(CommHandlerHarness.CloudSettings, tampered);

        ShouldBeUnauthorized(response);
        _harness.Engine.Calls.ShouldBeEmpty();
    }

    [Theory]
    [MemberData(nameof(GuardedRequests))]
    public async Task A_valid_signature_is_answered_and_the_response_is_signed_with_the_same_key(string kind)
    {
        var request = _harness.Sign(Request(kind), CommHandlerHarness.PrimaryKey);

        using var response = await _harness.SendAsync(CommHandlerHarness.CloudSettings, request);

        response.StatusCode.ShouldBe(200);
        response.Header("X-Inngest-SDK").ShouldNotBeNull();
        _harness.IsSignedWith(response, CommHandlerHarness.PrimaryKey).ShouldBeTrue();
    }

    [Theory]
    [MemberData(nameof(GuardedRequests))]
    public async Task During_key_rotation_the_fallback_key_is_accepted_and_signs_the_response(string kind)
    {
        var settings = CommHandlerHarness.CloudSettings with { SigningKeyFallback = CommHandlerHarness.FallbackKey };
        var request = _harness.Sign(Request(kind), CommHandlerHarness.FallbackKey);

        using var response = await _harness.SendAsync(settings, request);

        response.StatusCode.ShouldBe(200);
        _harness.IsSignedWith(response, CommHandlerHarness.FallbackKey).ShouldBeTrue();
        _harness.IsSignedWith(response, CommHandlerHarness.PrimaryKey).ShouldBeFalse();
    }

    [Fact]
    public async Task The_response_signature_is_made_at_response_time()
    {
        var request = _harness.Sign(new FakeServeRequest("POST", CallUrl, CallBody), CommHandlerHarness.PrimaryKey);
        _harness.Engine.Handler = (_, _) =>
        {
            _harness.Time.Advance(TimeSpan.FromSeconds(42));
            return Task.FromResult(FakeExecutionEngine.Result(200, "null"));
        };

        using var response = await _harness.SendAsync(CommHandlerHarness.CloudSettings, request);

        response.Header("X-Inngest-Signature").ShouldStartWith($"t={_harness.Time.GetUtcNow().ToUnixTimeSeconds()}&s=");
    }

    [Theory]
    [MemberData(nameof(GuardedRequests))]
    public async Task Dev_mode_accepts_unsigned_requests_and_signs_nothing(string kind)
    {
        var settings = CommHandlerHarness.DevSettings with { SigningKey = CommHandlerHarness.PrimaryKey };

        using var response = await _harness.SendAsync(settings, Request(kind));

        response.StatusCode.ShouldBe(200);
        response.Header("X-Inngest-Signature").ShouldBeNull();
    }

    [Fact]
    public async Task Unauthenticated_responses_strip_every_inngest_header_but_sdk_handled()
    {
        var settings = CommHandlerHarness.CloudSettings with { Environment = "branch-x" };
        var request = new FakeServeRequest("POST", CallUrl, CallBody).WithHeader("tracestate", "congo=t61rcWkgMzE");

        using var response = await _harness.SendAsync(settings, request);

        response.StatusCode.ShouldBe(401);
        response.HeaderNames().ShouldBe(["Content-Type", "X-Inngest-SDK-Handled", "tracestate"], ignoreOrder: true);
        response.Header("Content-Type").ShouldBe("application/json");
        response.Header("X-Inngest-SDK-Handled").ShouldBe("true");
    }

    [Fact]
    public async Task An_unsigned_405_reveals_nothing_either()
    {
        using var response = await _harness.SendAsync(CommHandlerHarness.CloudSettings, new FakeServeRequest("DELETE"));

        response.StatusCode.ShouldBe(405);
        response.HeaderNames().ShouldBe(["Content-Type", "X-Inngest-SDK-Handled"], ignoreOrder: true);
    }

    private static FakeServeRequest Request(string kind) => kind switch
    {
        "GET" => new FakeServeRequest("GET"),
        "POST" => new FakeServeRequest("POST", CallUrl, CallBody),
        "PUT in-band" => new FakeServeRequest("PUT", body: InBandBody).WithHeader("X-Inngest-Sync-Kind", "in_band"),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private void ShouldBeUnauthorized(ServeResponse response)
    {
        response.StatusCode.ShouldBe(401);
        response.BodyText().ShouldBe("""{"message":"Unauthorized"}""");
        response.Header("X-Inngest-SDK").ShouldBeNull();
        response.Header("X-Inngest-Req-Version").ShouldBeNull();
        response.Header("X-Inngest-Framework").ShouldBeNull();
        response.Header("X-Inngest-Signature").ShouldBeNull();
        response.Header("X-Inngest-SDK-Handled").ShouldBe("true");
        _harness.Engine.Calls.ShouldBeEmpty();
        _harness.Registry.Registrations.ShouldBeEmpty();
    }
}
