using System.Text;
using Inngest.Signing;
using Microsoft.Extensions.Time.Testing;

namespace Inngest.Tests.Signing;

// The edges of what the signer accepts: the five minute window, and the shapes of header it must not trust.
public sealed class SignatureWindowTests
{
    private static readonly DateTimeOffset Now = new(2026, 4, 22, 12, 0, 0, TimeSpan.Zero);
    private static readonly SigningKey Key = SigningKey.Create("signkey-test-deadbeefcafef00d")!;
    private static readonly byte[] Body = Encoding.UTF8.GetBytes("""{"event":{"name":"demo/event.sent","data":{}}}""");

    private readonly RequestSigner _verifier = new(new FakeTimeProvider(Now));

    // Signs as if the signature were made this many seconds before the verifier's clock says.
    private static string SignedSecondsAgo(int seconds, byte[]? body = null) =>
        new RequestSigner(new FakeTimeProvider(Now.AddSeconds(-seconds))).Sign(body ?? Body, Key);

    [Theory]
    [InlineData(300)]
    [InlineData(0)]
    [InlineData(-60)]
    public void A_timestamp_inside_the_window_is_accepted(int secondsAgo)
    {
        _verifier.Verify(SignedSecondsAgo(secondsAgo), Body, Key, fallbackKey: null).IsValid.ShouldBeTrue();
    }

    [Theory]
    [InlineData(301)]
    [InlineData(-301)]
    public void A_timestamp_outside_the_window_is_rejected_on_either_side(int secondsAgo)
    {
        var result = _verifier.Verify(SignedSecondsAgo(secondsAgo), Body, Key, fallbackKey: null);

        result.IsValid.ShouldBeFalse();
        result.Error!.ShouldContain("expired");
    }

    [Theory]
    [InlineData(-3600)]
    [InlineData(3600)]
    public void Expiry_is_ignored_in_both_directions_when_allowed(int secondsAgo)
    {
        _verifier.Verify(SignedSecondsAgo(secondsAgo), Body, Key, fallbackKey: null, allowExpired: true).IsValid.ShouldBeTrue();
    }

    [Fact]
    public void A_timestamp_written_in_hexadecimal_is_not_read_as_a_number()
    {
        var mac = SignedSecondsAgo(0).Split("&s=")[1];
        var hexTimestamp = $"0x{Now.ToUnixTimeSeconds():x}";

        _verifier.Verify($"t={hexTimestamp}&s={mac}", Body, Key, fallbackKey: null, allowExpired: true).IsValid.ShouldBeFalse();
    }

    [Theory]
    [InlineData("t=&s=")]
    [InlineData("t=&s=0000000000000000000000000000000000000000000000000000000000000000")]
    [InlineData("t=1&s=")]
    [InlineData("t=-1&s=0000000000000000000000000000000000000000000000000000000000000000")]
    public void A_header_with_empty_or_negative_values_is_rejected(string header)
    {
        _verifier.Verify(header, Body, Key, fallbackKey: null, allowExpired: true).IsValid.ShouldBeFalse();
    }

    [Fact]
    public void Changing_a_single_character_of_the_signature_is_enough_to_reject_it()
    {
        var header = SignedSecondsAgo(0);
        var separator = header.IndexOf("&s=", StringComparison.Ordinal) + 3;
        var flipped = header[..separator] + (header[separator] == '0' ? '1' : '0') + header[(separator + 1)..];

        _verifier.Verify(header, Body, Key, fallbackKey: null).IsValid.ShouldBeTrue();
        _verifier.Verify(flipped, Body, Key, fallbackKey: null).IsValid.ShouldBeFalse();
    }

    [Fact]
    public void Signing_is_deterministic_for_the_same_body_key_and_time()
    {
        SignedSecondsAgo(10).ShouldBe(SignedSecondsAgo(10));
    }

    [Fact]
    public void A_different_time_body_or_key_gives_a_different_signature()
    {
        var baseline = SignedSecondsAgo(10);

        SignedSecondsAgo(11).ShouldNotBe(baseline);
        SignedSecondsAgo(10, Encoding.UTF8.GetBytes("{}")).ShouldNotBe(baseline);
        new RequestSigner(new FakeTimeProvider(Now.AddSeconds(-10))).Sign(Body, SigningKey.Create("signkey-test-0123456789")!).ShouldNotBe(baseline);
    }

    [Theory]
    [InlineData("")]
    [InlineData("payload")]
    [InlineData("café 世界 🌍")]
    public void Any_body_signs_and_verifies_including_empty_and_multi_byte_text(string text)
    {
        var body = Encoding.UTF8.GetBytes(text);

        _verifier.Verify(SignedSecondsAgo(0, body), body, Key, fallbackKey: null).IsValid.ShouldBeTrue();
    }
}
