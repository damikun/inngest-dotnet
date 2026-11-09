using System.Text;
using Inngest.Signing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Inngest.Tests.Signing;

public sealed class InngestSignatureVerifierTests
{
    private const string Key = "signkey-prod-0a1b2c3d4e5f60718293a4b5c6d7e8f9";
    private const string Fallback = "signkey-prod-ffeeddccbbaa99887766554433221100";
    private static readonly DateTimeOffset Now = new(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);
    private static readonly byte[] Body = Encoding.UTF8.GetBytes("""{"event":{"name":"user/signed.up"}}""");

    private static string Sign(string key, DateTimeOffset at, byte[]? body = null) =>
        new RequestSigner(new FakeTimeProvider(at)).Sign(body ?? Body, SigningKey.Create(key)!);

    [Fact]
    public void A_signature_made_with_the_key_is_accepted()
    {
        var verifier = new InngestSignatureVerifier(Key, timeProvider: new FakeTimeProvider(Now));

        verifier.Verify(Sign(Key, Now), Body, out var error).ShouldBeTrue();
        error.ShouldBeNull();
    }

    [Fact]
    public void A_signature_made_with_the_fallback_key_is_accepted_while_the_key_is_rotated()
    {
        var verifier = new InngestSignatureVerifier(Key, Fallback, new FakeTimeProvider(Now));

        verifier.Verify(Sign(Fallback, Now), Body).ShouldBeTrue();
        new InngestSignatureVerifier(Key, timeProvider: new FakeTimeProvider(Now)).Verify(Sign(Fallback, Now), Body).ShouldBeFalse();
    }

    [Fact]
    public void A_changed_body_is_refused()
    {
        var verifier = new InngestSignatureVerifier(Key, timeProvider: new FakeTimeProvider(Now));

        verifier.Verify(Sign(Key, Now), Encoding.UTF8.GetBytes("{}"), out var error).ShouldBeFalse();
        error.ShouldBe("The request signature is invalid.");
    }

    [Fact]
    public void A_signature_older_than_five_minutes_is_refused()
    {
        var verifier = new InngestSignatureVerifier(Key, timeProvider: new FakeTimeProvider(Now));

        verifier.Verify(Sign(Key, Now.AddMinutes(-6)), Body, out var error).ShouldBeFalse();
        error!.ShouldContain("expired");
    }

    [Fact]
    public void A_missing_header_is_refused()
    {
        var verifier = new InngestSignatureVerifier(Key);

        verifier.Verify(null, Body, out var error).ShouldBeFalse();
        error!.ShouldContain("X-Inngest-Signature");
    }

    [Fact]
    public void A_key_is_required()
    {
        Should.Throw<ArgumentException>(() => new InngestSignatureVerifier(" "));
    }

    [Fact]
    public void The_registered_verifier_checks_with_the_app_keys()
    {
        using var services = Services(options =>
        {
            options.IsDev = false;
            options.SigningKey = Key;
            options.SigningKeyFallback = Fallback;
        });
        var verifier = services.GetRequiredService<InngestSignatureVerifier>();

        verifier.Verify(Sign(Fallback, DateTimeOffset.UtcNow), Body).ShouldBeTrue();
        verifier.Verify(Sign("signkey-prod-00", DateTimeOffset.UtcNow), Body).ShouldBeFalse();
    }

    [Fact]
    public void The_registered_verifier_accepts_every_request_in_dev_mode_as_the_endpoint_does()
    {
        using var services = Services(options => options.IsDev = true);

        services.GetRequiredService<InngestSignatureVerifier>().Verify(null, Body).ShouldBeTrue();
    }

    [Fact]
    public void The_registered_verifier_refuses_everything_in_cloud_mode_without_a_key()
    {
        using var services = Services(options => options.IsDev = false);

        services.GetRequiredService<InngestSignatureVerifier>().Verify(Sign(Key, DateTimeOffset.UtcNow), Body, out var error).ShouldBeFalse();
        error.ShouldBe("No signing key is configured.");
    }

    private static ServiceProvider Services(Action<InngestOptions> configure)
    {
        var services = new ServiceCollection();
        services.AddInngest(options =>
        {
            options.AppId = "shop";
            configure(options);
        });
        return services.BuildServiceProvider();
    }
}
