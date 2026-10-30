using Inngest.Internal;
using Inngest.Tests.Serving.Fakes;

namespace Inngest.Tests.Serving;

public sealed class IntrospectionTests
{
    private readonly CommHandlerHarness _harness = new();

    [Fact]
    public async Task Dev_mode_answers_the_unauthenticated_body()
    {
        using var response = await _harness.SendAsync(CommHandlerHarness.DevSettings, new FakeServeRequest("GET"));

        response.StatusCode.ShouldBe(200);
        response.BodyText().ShouldBe(
            """{"authentication_succeeded":null,"capabilities":{"trust_probe":"v1"},"function_count":2,"has_event_key":false,"has_signing_key":false,"has_signing_key_fallback":false,"mode":"dev","schema_version":"2024-05-24"}""");
    }

    [Fact]
    public async Task Dev_mode_reports_which_keys_are_set_without_revealing_them()
    {
        var settings = CommHandlerHarness.DevSettings with
        {
            EventKey = "event-key-123",
            SigningKey = CommHandlerHarness.PrimaryKey,
            SigningKeyFallback = CommHandlerHarness.FallbackKey,
        };

        using var response = await _harness.SendAsync(settings, new FakeServeRequest("GET"));

        response.BodyText().ShouldBe(
            """{"authentication_succeeded":null,"capabilities":{"trust_probe":"v1"},"function_count":2,"has_event_key":true,"has_signing_key":true,"has_signing_key_fallback":true,"mode":"dev","schema_version":"2024-05-24"}""");
    }

    [Fact]
    public async Task Dev_mode_ignores_signatures_and_signs_nothing()
    {
        var settings = CommHandlerHarness.DevSettings with { SigningKey = CommHandlerHarness.PrimaryKey };
        var request = new FakeServeRequest("GET").WithHeader("X-Inngest-Signature", "t=1&s=bogus");

        using var response = await _harness.SendAsync(settings, request);

        response.StatusCode.ShouldBe(200);
        response.BodyText().ShouldContain("\"authentication_succeeded\":null");
        response.Header("X-Inngest-Signature").ShouldBeNull();
    }

    [Fact]
    public async Task Dev_mode_responses_identify_the_sdk_for_dev_server_discovery()
    {
        using var response = await _harness.SendAsync(CommHandlerHarness.DevSettings, new FakeServeRequest("GET"));

        response.Header("X-Inngest-SDK").ShouldBe(SdkInfo.Header);
        response.Header("X-Inngest-SDK").ShouldStartWith("inngest-dotnet:v");
    }

    [Fact]
    public async Task Cloud_mode_answers_the_authenticated_body_to_a_signed_request()
    {
        var settings = CommHandlerHarness.CloudSettings with
        {
            EventKey = "event-key-123",
            SigningKeyFallback = CommHandlerHarness.FallbackKey,
            Environment = "branch-x",
            ServeOrigin = new Uri("https://shop.example.com"),
            ServePath = "/inngest",
        };
        var request = _harness.Sign(new FakeServeRequest("GET"), CommHandlerHarness.PrimaryKey);

        using var response = await _harness.SendAsync(settings, request);

        response.StatusCode.ShouldBe(200);
        response.BodyText().ShouldBe(
            $$"""{"api_origin":"https://api.inngest.com/","app_id":"shop","authentication_succeeded":true,"capabilities":{"trust_probe":"v1"},"env":"branch-x","event_api_origin":"https://inn.gs/","event_key_hash":"5d5a212001221cb2529bf266c37e615a785400000198a0d5b73a8b819603c6a3","extra":{"is_streaming":false},"framework":"test","function_count":2,"has_event_key":true,"has_signing_key":true,"has_signing_key_fallback":true,"mode":"cloud","schema_version":"2024-05-24","sdk_language":"dotnet","sdk_version":"{{SdkInfo.Version}}","serve_origin":"https://shop.example.com","serve_path":"/inngest","signing_key_fallback_hash":"02d449a31fbb","signing_key_hash":"66687aadf862"}""");
    }

    [Fact]
    public async Task Cloud_mode_says_whether_calls_are_streamed()
    {
        var request = _harness.Sign(new FakeServeRequest("GET"), CommHandlerHarness.PrimaryKey);

        using var response = await _harness.SendAsync(CommHandlerHarness.CloudSettings with { Streaming = true }, request);

        response.BodyText().ShouldContain("\"extra\":{\"is_streaming\":true}");
    }

    [Fact]
    public async Task Cloud_mode_writes_null_for_what_is_not_configured()
    {
        var request = _harness.Sign(new FakeServeRequest("GET"), CommHandlerHarness.PrimaryKey);

        using var response = await _harness.SendAsync(CommHandlerHarness.CloudSettings, request);

        response.BodyText().ShouldBe(
            $$"""{"api_origin":"https://api.inngest.com/","app_id":"shop","authentication_succeeded":true,"capabilities":{"trust_probe":"v1"},"env":null,"event_api_origin":"https://inn.gs/","event_key_hash":null,"extra":{"is_streaming":false},"framework":"test","function_count":2,"has_event_key":false,"has_signing_key":true,"has_signing_key_fallback":false,"mode":"cloud","schema_version":"2024-05-24","sdk_language":"dotnet","sdk_version":"{{SdkInfo.Version}}","serve_origin":null,"serve_path":null,"signing_key_fallback_hash":null,"signing_key_hash":"66687aadf862"}""");
    }

    [Fact]
    public async Task Cloud_mode_signs_the_introspection_body()
    {
        var request = _harness.Sign(new FakeServeRequest("GET"), CommHandlerHarness.PrimaryKey);

        using var response = await _harness.SendAsync(CommHandlerHarness.CloudSettings, request);

        _harness.IsSignedWith(response, CommHandlerHarness.PrimaryKey).ShouldBeTrue();
    }

    [Fact]
    public async Task Cloud_mode_verifies_a_GET_over_an_empty_body()
    {
        // Signed over "", as Inngest signs GETs, although this request happens to carry a body.
        var request = new FakeServeRequest("GET", body: """{"ignored":true}""")
            .WithHeader("X-Inngest-Signature", _harness.Signer.Sign([], CommHandlerHarness.PrimaryKey));

        using var response = await _harness.SendAsync(CommHandlerHarness.CloudSettings, request);

        response.StatusCode.ShouldBe(200);
    }

    [Fact]
    public async Task Cloud_mode_answers_401_without_a_valid_signature()
    {
        using var response = await _harness.SendAsync(CommHandlerHarness.CloudSettings, new FakeServeRequest("GET"));

        response.StatusCode.ShouldBe(401);
        response.BodyText().ShouldBe("""{"message":"Unauthorized"}""");
    }

    [Fact]
    public async Task The_function_count_is_what_the_registry_holds()
    {
        _harness.Registry.Definitions.RemoveAt(1);

        using var response = await _harness.SendAsync(CommHandlerHarness.DevSettings, new FakeServeRequest("GET"));

        response.BodyText().ShouldContain("\"function_count\":1,");
    }
}
