using System.Text;

namespace Inngest.Tests.Webhooks;

public sealed class StandardWebhooksVerifierTests
{
    // The vectors of the Standard Webhooks test suites of every language (standard-webhooks/standard-webhooks,
    // libraries/*), which sign the same message with the same secret.
    private const string Secret = "whsec_MfKQ9r8GKYqrTwjUPD8ILPZIo2LaLaSw";
    private const string Id = "msg_p5jXN8AQM9LWM0D4loKWxJek";
    private const long Timestamp = 1614265330;
    private const string Body = """{"test": 2432232314}""";
    private const string BodySignature = "v1,g0hM9SsE+OTPJTGt/tmIKtSyZlE3uFJELVlNIOLJ1OE=";
    private const string BinarySignature = "v1,sEcZ042G8Crqgxbybb3y0eDxQR3TcfYsk11I1XULhSI=";

    private const string Other = "whsec_Rm9yIHRlc3RzIG9ubHksIHRoZSBvbGQgc2VjcmV0Lg==";

    private static readonly DateTimeOffset Arrival = DateTimeOffset.FromUnixTimeSeconds(Timestamp);

    private static TestWebhookRequest Signed(string signature = BodySignature, string body = Body, string id = Id, long timestamp = Timestamp) =>
        new TestWebhookRequest(body)
            .With("webhook-id", id)
            .With("webhook-timestamp", timestamp.ToString())
            .With("webhook-signature", signature);

    [Fact]
    public void The_published_vector_verifies_and_gives_the_delivery_id()
    {
        var result = new StandardWebhooksVerifier(Secret).Verify(Signed(), Arrival);

        result.IsValid.ShouldBeTrue();
        result.DeliveryId.ShouldBe(Id);
        result.Reason.ShouldBeNull();
    }

    [Fact]
    public void A_body_that_is_not_text_verifies_byte_for_byte()
    {
        var body = Enumerable.Range(0, 256).Select(value => (byte)value).ToArray();
        var request = new TestWebhookRequest(body)
            .With("webhook-id", Id)
            .With("webhook-timestamp", Timestamp.ToString())
            .With("webhook-signature", BinarySignature);

        new StandardWebhooksVerifier(Secret).Verify(request, Arrival).IsValid.ShouldBeTrue();
    }

    [Fact]
    public void The_svix_headers_that_Clerk_and_Resend_send_are_read_too()
    {
        var request = new TestWebhookRequest(Body)
            .With("svix-id", Id)
            .With("svix-timestamp", Timestamp.ToString())
            .With("svix-signature", BodySignature);

        var result = new StandardWebhooksVerifier(Secret).Verify(request, Arrival);

        result.IsValid.ShouldBeTrue();
        result.DeliveryId.ShouldBe(Id);
    }

    [Fact]
    public void A_family_of_headers_with_one_missing_is_not_mixed_with_the_other()
    {
        var request = new TestWebhookRequest(Body)
            .With("webhook-id", Id)
            .With("svix-timestamp", Timestamp.ToString())
            .With("svix-signature", BodySignature);

        new StandardWebhooksVerifier(Secret).Verify(request, Arrival).IsValid.ShouldBeFalse();
    }

    [Fact]
    public void The_secret_may_be_written_without_its_prefix()
    {
        new StandardWebhooksVerifier(Secret["whsec_".Length..]).Verify(Signed(), Arrival).IsValid.ShouldBeTrue();
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(300, true)]
    [InlineData(301, false)]
    [InlineData(-300, true)]
    [InlineData(-301, false)]
    public void The_timestamp_may_be_five_minutes_from_the_arrival_in_either_direction(int secondsAfterTimestamp, bool accepted)
    {
        var result = new StandardWebhooksVerifier(Secret).Verify(Signed(), Arrival.AddSeconds(secondsAfterTimestamp));

        result.IsValid.ShouldBe(accepted);
        if (!accepted)
        {
            result.Reason.ShouldNotBeNull().ShouldContain("tolerance");
        }
    }

    [Fact]
    public void The_tolerance_can_be_changed()
    {
        var verifier = new StandardWebhooksVerifier(Secret, TimeSpan.FromSeconds(10));

        verifier.Verify(Signed(), Arrival.AddSeconds(10)).IsValid.ShouldBeTrue();
        verifier.Verify(Signed(), Arrival.AddSeconds(11)).IsValid.ShouldBeFalse();
    }

    [Fact]
    public void A_stale_request_with_a_wrong_signature_is_reported_as_a_wrong_signature()
    {
        var result = new StandardWebhooksVerifier(Secret).Verify(Signed("v1,AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA="), Arrival.AddHours(1));

        result.IsValid.ShouldBeFalse();
        result.Reason.ShouldNotBeNull().ShouldContain("signature");
    }

    [Fact]
    public void A_signature_made_with_either_of_two_secrets_is_accepted_while_a_secret_is_replaced()
    {
        var verifier = new StandardWebhooksVerifier([Secret, Other]);
        var withOther = Signing.StandardWebhooks(Other, Id, Timestamp, Encoding.UTF8.GetBytes(Body));

        verifier.Verify(Signed(), Arrival).IsValid.ShouldBeTrue();
        verifier.Verify(Signed(withOther), Arrival).IsValid.ShouldBeTrue();
        verifier.Verify(Signed($"{withOther} {BodySignature}"), Arrival).IsValid.ShouldBeTrue();
        new StandardWebhooksVerifier(Secret).Verify(Signed(withOther), Arrival).IsValid.ShouldBeFalse();
    }

    [Fact]
    public void Other_versions_and_malformed_entries_before_a_valid_signature_are_skipped()
    {
        var verifier = new StandardWebhooksVerifier(Secret);
        var other = "v1,Ceo5qEr07ixe2NLpvHk3FH9bwy/WavXrAFQ/9tdO6mc=";

        verifier.Verify(Signed($"{other} v2,{other[3..]} v1a,{other[3..]} {BodySignature} {other}"), Arrival).IsValid.ShouldBeTrue();
        verifier.Verify(Signed($"invalid {BodySignature}"), Arrival).IsValid.ShouldBeTrue();
        verifier.Verify(Signed($"v1, v1,,, v1 ,v1 {BodySignature}"), Arrival).IsValid.ShouldBeTrue();
        verifier.Verify(Signed($"  {BodySignature}\t "), Arrival).IsValid.ShouldBeTrue();
    }

    [Theory]
    [InlineData("v2")]
    [InlineData("v1a")]
    [InlineData("V1")]
    public void The_right_signature_under_another_version_is_not_accepted(string version)
    {
        var result = new StandardWebhooksVerifier(Secret).Verify(Signed($"{version},{BodySignature[3..]}"), Arrival);

        result.IsValid.ShouldBeFalse();
    }

    [Fact]
    public void Nothing_about_the_message_can_change_after_it_was_signed()
    {
        var verifier = new StandardWebhooksVerifier(Secret);

        verifier.Verify(Signed(body: """{"test": 2432232315}"""), Arrival).IsValid.ShouldBeFalse();
        verifier.Verify(Signed(body: Body + " "), Arrival).IsValid.ShouldBeFalse();
        verifier.Verify(Signed(id: "msg_p5jXN8AQM9LWM0D4loKWxJeK"), Arrival).IsValid.ShouldBeFalse();
        verifier.Verify(Signed(timestamp: Timestamp + 1), Arrival).IsValid.ShouldBeFalse();
        verifier.Verify(Signed(), Arrival).IsValid.ShouldBeTrue();
    }

    [Theory]
    [InlineData("webhook-id")]
    [InlineData("webhook-timestamp")]
    [InlineData("webhook-signature")]
    public void A_missing_header_rejects_the_request(string missing)
    {
        var request = new TestWebhookRequest(Body);
        foreach (var (name, value) in new[] { ("webhook-id", Id), ("webhook-timestamp", "1614265330"), ("webhook-signature", BodySignature) })
        {
            if (name != missing)
            {
                request.With(name, value);
            }
        }

        var result = new StandardWebhooksVerifier(Secret).Verify(request, Arrival);

        result.IsValid.ShouldBeFalse();
        result.Reason.ShouldNotBeNull().ShouldContain("missing");
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("abc")]
    [InlineData("-5")]
    [InlineData("+1614265330")]
    [InlineData("1e9")]
    [InlineData("1614265330.5")]
    [InlineData("1614 265330")]
    [InlineData("99999999999999999999")]
    [InlineData("9223372036854775808")]
    [InlineData("９９９")]
    public void A_timestamp_that_is_not_digits_is_rejected_without_an_exception(string timestamp)
    {
        var request = Signed().With("webhook-timestamp", timestamp);

        new StandardWebhooksVerifier(Secret).Verify(request, Arrival).IsValid.ShouldBeFalse();
    }

    [Theory]
    [InlineData("msg.1")]
    [InlineData("msg 1")]
    [InlineData("msg\n1")]
    [InlineData("mésg")]
    public void An_id_the_signed_text_could_be_read_differently_with_is_rejected_even_when_it_was_signed(string id)
    {
        var signature = Signing.StandardWebhooks(Secret, id, Timestamp, Encoding.UTF8.GetBytes(Body));

        new StandardWebhooksVerifier(Secret).Verify(Signed(signature, id: id), Arrival).IsValid.ShouldBeFalse();
    }

    [Fact]
    public void An_id_longer_than_the_limit_is_rejected_even_when_it_was_signed()
    {
        var id = new string('a', 300);
        var signature = Signing.StandardWebhooks(Secret, id, Timestamp, Encoding.UTF8.GetBytes(Body));

        new StandardWebhooksVerifier(Secret).Verify(Signed(signature, id: id), Arrival).IsValid.ShouldBeFalse();
    }

    [Theory]
    [InlineData("v1,")]
    [InlineData("v1,!!!!")]
    [InlineData("v1,AAAA")]
    [InlineData("v1,====")]
    [InlineData("v1,g0hM9SsE+OTPJTGt/tmIKtSyZlE3uFJELVlNIOLJ1OE")]
    [InlineData("v1,g0hM9SsE+OTPJTGt/tmIKtSyZlE3uFJELVlNIOLJ1OE=extra")]
    [InlineData("v1,g0hM9SsE+OTPJTGt/tmIKtSyZlE3uFJELVlNIOLJ1OEg0hM9SsE+OTPJTGt/tmIKtSyZlE3uFJELVlNIOLJ1OE=")]
    [InlineData("v1")]
    [InlineData(",")]
    [InlineData("🙂,🙂")]
    public void A_signature_that_is_not_base64_of_a_digest_is_rejected_without_an_exception(string signature)
    {
        new StandardWebhooksVerifier(Secret).Verify(Signed(signature), Arrival).IsValid.ShouldBeFalse();
    }

    [Fact]
    public void Constructing_with_a_bad_secret_fails_without_putting_it_in_the_message()
    {
        foreach (var secret in new[] { "", "   ", "whsec_", "whsec_!!!not-base64!!!", "whsec_a" })
        {
            var error = Should.Throw<ArgumentException>(() => new StandardWebhooksVerifier(secret));
            error.Message.ShouldNotContain("not-base64");
        }

        Should.Throw<ArgumentException>(() => new StandardWebhooksVerifier(Array.Empty<string>()));
        Should.Throw<ArgumentOutOfRangeException>(() => new StandardWebhooksVerifier(Secret, TimeSpan.Zero));
        Should.Throw<ArgumentOutOfRangeException>(() => new StandardWebhooksVerifier(Secret, TimeSpan.FromSeconds(-1)));
    }

    [Fact]
    public void A_default_verification_is_not_valid()
    {
        default(WebhookVerification).IsValid.ShouldBeFalse();
    }
}
