using Inngest.Configuration;
using Inngest.Serving;
using Inngest.Signing;
using Inngest.Tests.Serving.Fakes;
using Inngest.Tests.Signing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Inngest.Tests.Serving;

// A call whose signature was made by an Inngest server (the vectors of ServerSignatureTests), sent through the whole
// handler with the clock set to the moment it was signed.
public sealed class ServerSignedCallTests
{
    private const string Url = "http://localhost:3000/api/inngest?fnId=shop-send-email&stepId=step";

    private readonly CommHandlerHarness _harness = new();
    private readonly FakeTimeProvider _time = new(DateTimeOffset.FromUnixTimeSeconds(ServerSignatureTests.SignedAt));

    [Fact]
    public async Task A_call_signed_by_the_server_runs_and_is_answered_with_a_signature_of_the_same_key()
    {
        var settings = CloudSettings(ServerSignatureTests.Key);

        using var response = await Handler(settings).HandleAsync(Call(), TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(200);
        _harness.Engine.Calls.ShouldHaveSingleItem().BodyText.ShouldBe(ServerSignatureTests.Body);
        new RequestSigner(_time)
            .Verify(response.Header("X-Inngest-Signature"), response.Body.Span, SigningKey.Create(ServerSignatureTests.Key)!, fallbackKey: null)
            .IsValid.ShouldBeTrue();
    }

    [Fact]
    public async Task During_key_rotation_the_fallback_key_accepts_the_call_and_signs_the_answer()
    {
        var settings = CloudSettings("fake") with { SigningKeyFallback = SigningKey.Create(ServerSignatureTests.Key) };

        using var response = await Handler(settings).HandleAsync(Call(), TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(200);
        new RequestSigner(_time)
            .Verify(response.Header("X-Inngest-Signature"), response.Body.Span, SigningKey.Create(ServerSignatureTests.Key)!, fallbackKey: null)
            .IsValid.ShouldBeTrue();
    }

    [Fact]
    public async Task A_call_is_refused_when_neither_key_made_the_signature()
    {
        var settings = CloudSettings("fake") with { SigningKeyFallback = SigningKey.Create("another-fake") };

        using var response = await Handler(settings).HandleAsync(Call(), TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(401);
        response.BodyText().ShouldBe("""{"message":"Unauthorized"}""");
        _harness.Engine.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_call_signed_by_the_server_is_refused_five_minutes_later()
    {
        var settings = CloudSettings(ServerSignatureTests.Key);
        _time.Advance(TimeSpan.FromSeconds(301));

        using var response = await Handler(settings).HandleAsync(Call(), TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(401);
        _harness.Engine.Calls.ShouldBeEmpty();
    }

    private static InngestSettings CloudSettings(string key) =>
        CommHandlerHarness.CloudSettings with { SigningKey = SigningKey.Create(key) };

    private static FakeServeRequest Call() =>
        new FakeServeRequest("POST", Url, ServerSignatureTests.Body).WithHeader("X-Inngest-Signature", ServerSignatureTests.Header);

    private InngestCommHandler Handler(InngestSettings settings) =>
        new(
            settings,
            _harness.Registry,
            _harness.Engine,
            _harness.Api,
            new RequestSigner(_time),
            _harness.RootServices.GetRequiredService<IServiceScopeFactory>(),
            _harness.Logger);
}
