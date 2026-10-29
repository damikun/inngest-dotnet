using System.Text;
using Inngest.Signing;
using Microsoft.Extensions.Time.Testing;

namespace Inngest.Tests.Signing;

public sealed class RequestSignerTests
{
    private static readonly DateTimeOffset SignedAt = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);
    private static readonly byte[] Body = Encoding.UTF8.GetBytes("""{"hello":"world"}""");

    // HMAC-SHA256(key "1234567890", body + "1700000000"), computed independently with Python's hmac module.
    private const string ExpectedSignature = "33229854a57ccf6722b718a6493094cf1b6b00e74d437077c5432e20345971ab";

    private readonly FakeTimeProvider _time = new(SignedAt);

    [Fact]
    public void Sign_uses_the_key_without_its_prefix_as_utf8_text()
    {
        var signer = new RequestSigner(_time);

        var header = signer.Sign(Body, SigningKey.Create("signkey-test-1234567890")!);

        header.ShouldBe($"t=1700000000&s={ExpectedSignature}");
    }

    [Fact]
    public void Verify_accepts_a_valid_signature()
    {
        var key = SigningKey.Create("signkey-test-1234567890")!;

        var result = new RequestSigner(_time).Verify($"t=1700000000&s={ExpectedSignature}", Body, key, fallbackKey: null);

        result.IsValid.ShouldBeTrue();
        result.KeyUsed.ShouldBeSameAs(key);
    }

    [Fact]
    public void Verify_falls_back_to_the_fallback_key_during_rotation()
    {
        var current = SigningKey.Create("signkey-prod-aaaaaaaa")!;
        var previous = SigningKey.Create("signkey-prod-1234567890")!;

        var result = new RequestSigner(_time).Verify($"t=1700000000&s={ExpectedSignature}", Body, current, previous);

        result.IsValid.ShouldBeTrue();
        result.KeyUsed.ShouldBeSameAs(previous);
    }

    [Fact]
    public void Verify_rejects_a_tampered_body()
    {
        var key = SigningKey.Create("signkey-test-1234567890")!;
        var tampered = Encoding.UTF8.GetBytes("""{"hello":"there"}""");

        var result = new RequestSigner(_time).Verify($"t=1700000000&s={ExpectedSignature}", tampered, key, fallbackKey: null);

        result.IsValid.ShouldBeFalse();
        result.KeyUsed.ShouldBeNull();
    }

    [Theory]
    [InlineData(-301)]
    [InlineData(301)]
    public void Verify_rejects_timestamps_more_than_five_minutes_away(int offsetSeconds)
    {
        var time = new FakeTimeProvider(SignedAt.AddSeconds(offsetSeconds));
        var key = SigningKey.Create("signkey-test-1234567890")!;

        var result = new RequestSigner(time).Verify($"t=1700000000&s={ExpectedSignature}", Body, key, fallbackKey: null);

        result.IsValid.ShouldBeFalse();
        result.Error!.ShouldContain("expired");
    }

    [Fact]
    public void Verify_ignores_expiry_when_allowed()
    {
        _time.SetUtcNow(SignedAt.AddHours(1));
        var key = SigningKey.Create("signkey-test-1234567890")!;

        var result = new RequestSigner(_time).Verify($"t=1700000000&s={ExpectedSignature}", Body, key, null, allowExpired: true);

        result.IsValid.ShouldBeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("s=abc")]
    [InlineData("t=1700000000")]
    [InlineData("t=abc&s=33229854a57ccf6722b718a6493094cf1b6b00e74d437077c5432e20345971ab")]
    [InlineData("t=1700000000&s=zz229854a57ccf6722b718a6493094cf1b6b00e74d437077c5432e20345971ab")]
    [InlineData("t=1700000000&s=3322")]
    [InlineData("t=99999999999999&s=33229854a57ccf6722b718a6493094cf1b6b00e74d437077c5432e20345971ab")]
    [InlineData("t=9223372036854775807&s=33229854a57ccf6722b718a6493094cf1b6b00e74d437077c5432e20345971ab")]
    public void Verify_rejects_missing_or_malformed_headers(string? header)
    {
        var key = SigningKey.Create("signkey-test-1234567890")!;

        new RequestSigner(_time).Verify(header, Body, key, fallbackKey: null).IsValid.ShouldBeFalse();
    }

    [Fact]
    public void Signed_responses_verify_with_the_same_key()
    {
        var key = SigningKey.Create("signkey-prod-deadbeefcafef00d")!;
        var signer = new RequestSigner(_time);

        var header = signer.Sign(Body, key);

        signer.Verify(header, Body, key, fallbackKey: null).IsValid.ShouldBeTrue();
    }
}
