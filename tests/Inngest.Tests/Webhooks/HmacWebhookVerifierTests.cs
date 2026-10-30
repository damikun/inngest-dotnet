using System.Security.Cryptography;

namespace Inngest.Tests.Webhooks;

public sealed class HmacWebhookVerifierTests
{
    // Slack's worked example in "Verifying requests from Slack": the signature is v0= and the hex HMAC-SHA256 of
    // v0:{timestamp}:{body}, and Slack tells apps to reject a timestamp more than five minutes from their own clock.
    private const string SlackSecret = "8f742231b10e8888abcd99yyyzzz85a5";
    private const long SlackTimestamp = 1531420618;
    private const string SlackBody = "token=xyzz0WbapA4vBCDEFasx0q6G&team_id=T1DC2JH3J&team_domain=testteamnow&channel_id=G8PSS9T3V&channel_name=foobar&user_id=U2CERLKJA&user_name=roadrunner&command=%2Fwebhook-collect&text=&response_url=https%3A%2F%2Fhooks.slack.com%2Fcommands%2FT1DC2JH3J%2F397700885554%2F96rGlfmibIGlgcZRskXaIFfN&trigger_id=398738663015.47445629121.803a0bc887a14d10d2c447fce8b6703c";
    private const string SlackSignature = "v0=a2114d57b48eac39b9ad189dd8316235a7b4a8d21a10bd27519666489c69b503";

    private static readonly HmacWebhookOptions Slack = new()
    {
        SignatureHeader = "X-Slack-Signature",
        SignaturePrefix = "v0=",
        TimestampHeader = "X-Slack-Request-Timestamp",
        SignedContentPrefix = "v0:",
        TimestampSeparator = ":",
    };

    private static readonly DateTimeOffset SlackArrival = DateTimeOffset.FromUnixTimeSeconds(SlackTimestamp);

    // GitHub's example again, signed with other hashes by Python's hmac.
    private const string Secret = "It's a Secret to Everybody";
    private const string Body = "Hello, World!";
    private const string Sha1Hex = "01dc10d0c83e72ed246219cdd91669667fe2ca59";
    private const string Sha384Hex = "8ae941e23ce94331d5a5986b762bbdb2039fec41c133145da82908f381ba612379307f45738b252be379438ef60839c2";
    private const string Sha512Base64 = "Ee01WmF+mBNOhCASp5RMz1nBAlbLGCNXvX46QgE/8Hw3b4wUz1zBkj2iC1HWQlay+4678QCqZ6YTJvYf6oERvA==";

    private static TestWebhookRequest SlackRequest(string signature = SlackSignature, string timestamp = "1531420618", string body = SlackBody) =>
        new TestWebhookRequest(body).With("X-Slack-Signature", signature).With("X-Slack-Request-Timestamp", timestamp);

    [Fact]
    public void Slacks_worked_example_verifies_with_options_alone()
    {
        new HmacWebhookVerifier(Slack, SlackSecret).Verify(SlackRequest(), SlackArrival).IsValid.ShouldBeTrue();
    }

    [Theory]
    [InlineData(300, true)]
    [InlineData(301, false)]
    [InlineData(-300, true)]
    [InlineData(-301, false)]
    public void The_signed_timestamp_must_be_within_the_tolerance(int secondsAfterTimestamp, bool accepted)
    {
        new HmacWebhookVerifier(Slack, SlackSecret).Verify(SlackRequest(), SlackArrival.AddSeconds(secondsAfterTimestamp)).IsValid.ShouldBe(accepted);
    }

    [Fact]
    public void The_tolerance_can_be_changed()
    {
        var verifier = new HmacWebhookVerifier(Slack with { Tolerance = TimeSpan.FromHours(1) }, SlackSecret);

        verifier.Verify(SlackRequest(), SlackArrival.AddMinutes(60)).IsValid.ShouldBeTrue();
        verifier.Verify(SlackRequest(), SlackArrival.AddMinutes(61)).IsValid.ShouldBeFalse();
    }

    [Fact]
    public void The_timestamp_is_part_of_the_signed_text_so_a_replay_cannot_make_itself_recent()
    {
        var verifier = new HmacWebhookVerifier(Slack, SlackSecret);

        verifier.Verify(SlackRequest(timestamp: "1531420619"), SlackArrival).IsValid.ShouldBeFalse();
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("-1")]
    [InlineData("1531420618.5")]
    [InlineData("99999999999999999999")]
    public void A_timestamp_that_is_not_digits_is_rejected_without_an_exception(string timestamp)
    {
        new HmacWebhookVerifier(Slack, SlackSecret).Verify(SlackRequest(timestamp: timestamp), SlackArrival).IsValid.ShouldBeFalse();
    }

    [Fact]
    public void A_missing_signature_or_timestamp_rejects_the_request()
    {
        var verifier = new HmacWebhookVerifier(Slack, SlackSecret);

        verifier.Verify(new TestWebhookRequest(SlackBody).With("X-Slack-Signature", SlackSignature), SlackArrival).IsValid.ShouldBeFalse();
        verifier.Verify(new TestWebhookRequest(SlackBody).With("X-Slack-Request-Timestamp", "1531420618"), SlackArrival).IsValid.ShouldBeFalse();
    }

    [Theory]
    [InlineData("SHA1", Sha1Hex, 0)]
    [InlineData("SHA384", Sha384Hex, 0)]
    [InlineData("SHA512", Sha512Base64, 1)]
    public void The_hash_and_the_encoding_come_from_the_options(string algorithm, string signature, int encoding)
    {
        var options = new HmacWebhookOptions
        {
            SignatureHeader = "X-Signature",
            Algorithm = new HashAlgorithmName(algorithm),
            SignatureEncoding = (HmacSignatureEncoding)encoding,
        };

        new HmacWebhookVerifier(options, Secret).Verify(new TestWebhookRequest(Body).With("X-Signature", signature), SlackArrival).IsValid.ShouldBeTrue();
        new HmacWebhookVerifier(options, "another secret").Verify(new TestWebhookRequest(Body).With("X-Signature", signature), SlackArrival).IsValid.ShouldBeFalse();
    }

    [Fact]
    public void A_header_may_hold_several_signatures_separated_by_commas_or_white_space()
    {
        var options = new HmacWebhookOptions { SignatureHeader = "X-Signature", Algorithm = HashAlgorithmName.SHA1 };
        var verifier = new HmacWebhookVerifier(options, Secret);
        var other = new string('a', 40);

        verifier.Verify(new TestWebhookRequest(Body).With("X-Signature", $"{other}, {Sha1Hex}"), SlackArrival).IsValid.ShouldBeTrue();
        verifier.Verify(new TestWebhookRequest(Body).With("X-Signature", $"{other} {Sha1Hex}"), SlackArrival).IsValid.ShouldBeTrue();
        verifier.Verify(new TestWebhookRequest(Body).With("X-Signature", $"{other},{other}"), SlackArrival).IsValid.ShouldBeFalse();
    }

    [Fact]
    public void Several_secrets_are_accepted_while_a_secret_is_replaced()
    {
        var options = new HmacWebhookOptions { SignatureHeader = "X-Signature", Algorithm = HashAlgorithmName.SHA1 };

        new HmacWebhookVerifier(options, ["the new secret", Secret]).Verify(new TestWebhookRequest(Body).With("X-Signature", Sha1Hex), SlackArrival).IsValid.ShouldBeTrue();
    }

    [Fact]
    public void Without_a_timestamp_header_the_time_of_arrival_does_not_matter_and_the_delivery_id_header_is_used()
    {
        var options = new HmacWebhookOptions
        {
            SignatureHeader = "X-Signature",
            Algorithm = HashAlgorithmName.SHA1,
            DeliveryIdHeader = "X-Delivery",
        };
        var request = new TestWebhookRequest(Body).With("X-Signature", Sha1Hex).With("X-Delivery", "d-1");

        var result = new HmacWebhookVerifier(options, Secret).Verify(request, SlackArrival.AddYears(5));

        result.IsValid.ShouldBeTrue();
        result.DeliveryId.ShouldBe("d-1");
    }

    [Theory]
    [InlineData("")]
    [InlineData("v0=")]
    [InlineData("v0=a2114d57")]
    [InlineData("v0=zz114d57b48eac39b9ad189dd8316235a7b4a8d21a10bd27519666489c69b503")]
    [InlineData("a2114d57b48eac39b9ad189dd8316235a7b4a8d21a10bd27519666489c69b503")]
    [InlineData("v1=a2114d57b48eac39b9ad189dd8316235a7b4a8d21a10bd27519666489c69b503")]
    [InlineData("v0=🙂")]
    public void A_signature_that_does_not_fit_is_rejected_without_an_exception(string signature)
    {
        new HmacWebhookVerifier(Slack, SlackSecret).Verify(SlackRequest(signature), SlackArrival).IsValid.ShouldBeFalse();
    }

    [Fact]
    public void Options_that_cannot_work_fail_the_constructor()
    {
        Should.Throw<ArgumentException>(() => new HmacWebhookVerifier(new HmacWebhookOptions { SignatureHeader = " " }, Secret));
        Should.Throw<ArgumentException>(() => new HmacWebhookVerifier(new HmacWebhookOptions { SignatureHeader = "X", Algorithm = HashAlgorithmName.MD5 }, Secret));
        Should.Throw<ArgumentException>(() => new HmacWebhookVerifier(new HmacWebhookOptions { SignatureHeader = "X", SignedContentPrefix = new string('v', 200) }, Secret));
        Should.Throw<ArgumentOutOfRangeException>(() => new HmacWebhookVerifier(new HmacWebhookOptions { SignatureHeader = "X", Tolerance = TimeSpan.Zero }, Secret));
        Should.Throw<ArgumentOutOfRangeException>(() => new HmacWebhookVerifier(new HmacWebhookOptions { SignatureHeader = "X", SignatureEncoding = (HmacSignatureEncoding)7 }, Secret));
        Should.Throw<ArgumentException>(() => new HmacWebhookVerifier(new HmacWebhookOptions { SignatureHeader = "X" }, ""));
    }
}
