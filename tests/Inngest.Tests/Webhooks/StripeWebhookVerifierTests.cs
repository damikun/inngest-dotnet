using System.Text;

namespace Inngest.Tests.Webhooks;

public sealed class StripeWebhookVerifierTests
{
    // Signed by hand with Python's hmac for the algorithm in https://docs.stripe.com/webhooks#verify-manually: the HMAC-SHA256
    // of "{t}.{body}", keyed with the text of the secret, in hex.
    private const string Secret = "whsec_Hk3pX9wZq2LmN7vR4tYb8cDf6gJa1sE5";
    private const string OldSecret = "whsec_Qw8eR2tY6uI0oP4aS7dF1gH3jK5lZ9xC";
    private const long Timestamp = 1686089970;
    private const string Signature = "4dc21d39494ce7358af90407e09cefa80fea054e96043dec4d5ee97d991dba45";
    private const string OldSignature = "49ae86d1e8dcdff2b4b52b0cf73bdb3d79e67dfd80519bd2573842290f2022fa";

    private const string Body = """{"id":"evt_1NG8Du2eZvKYlo2CUI8iWOzQ","object":"event","api_version":"2022-11-15","created":1686089970,"data":{"object":{"id":"pi_3NG8Dt2eZvKYlo2C0jbDz8Qa","object":"payment_intent","amount":2000,"currency":"usd","status":"succeeded"}},"livemode":false,"pending_webhooks":1,"request":{"id":null,"idempotency_key":null},"type":"payment_intent.succeeded"}""";

    private static readonly DateTimeOffset Arrival = DateTimeOffset.FromUnixTimeSeconds(Timestamp);

    private static TestWebhookRequest Signed(string header, string body = Body) =>
        new TestWebhookRequest(body).With("Stripe-Signature", header);

    [Fact]
    public void The_signature_the_algorithm_gives_verifies()
    {
        var result = new StripeWebhookVerifier(Secret).Verify(Signed($"t={Timestamp},v1={Signature}"), Arrival);

        result.IsValid.ShouldBeTrue();

        // The id of the event is in the body, which is signed, so there is no delivery id in a header.
        result.DeliveryId.ShouldBeNull();
    }

    [Fact]
    public void The_test_helper_computes_the_same_signature()
    {
        Signing.Stripe(Secret, Timestamp, Encoding.UTF8.GetBytes(Body)).ShouldBe(Signature);
    }

    [Fact]
    public void The_secret_is_used_as_text_and_not_decoded_as_base64()
    {
        // whsec_ followed by base64, like Svix's secrets, but Stripe signs with the text.
        var decoded = Convert.FromBase64String(Secret["whsec_".Length..]);
        var wrongSignature = Signing.Hex(System.Security.Cryptography.HMACSHA256.HashData(decoded, Encoding.UTF8.GetBytes($"{Timestamp}.{Body}")));

        new StripeWebhookVerifier(Secret).Verify(Signed($"t={Timestamp},v1={wrongSignature}"), Arrival).IsValid.ShouldBeFalse();
    }

    [Theory]
    [InlineData("t={0},v1={1}")]
    [InlineData("v1={1},t={0}")]
    [InlineData("t={0}, v1={1}")]
    [InlineData("  t={0} ,  v1={1}  ")]
    [InlineData("t={0},v0=6ffbb59b2300aae63f272406069a9788598b792a944a07aba816edb039989a39,v1={1}")]
    [InlineData("t={0},v1=00,v1={1},v1=zz")]
    [InlineData("t={0},,garbage,=,v1={1},")]
    public void The_pairs_may_come_in_any_order_with_other_schemes_and_junk_around_them(string format)
    {
        var header = string.Format(format, Timestamp, Signature);

        new StripeWebhookVerifier(Secret).Verify(Signed(header), Arrival).IsValid.ShouldBeTrue();
    }

    [Fact]
    public void A_rolled_secret_signs_with_both_secrets_and_either_one_passes()
    {
        var both = $"t={Timestamp},v1={OldSignature},v1={Signature}";

        new StripeWebhookVerifier(Secret).Verify(Signed(both), Arrival).IsValid.ShouldBeTrue();
        new StripeWebhookVerifier(OldSecret).Verify(Signed(both), Arrival).IsValid.ShouldBeTrue();
        new StripeWebhookVerifier([Secret, OldSecret]).Verify(Signed(both), Arrival).IsValid.ShouldBeTrue();
        new StripeWebhookVerifier([OldSecret, Secret]).Verify(Signed($"t={Timestamp},v1={Signature}"), Arrival).IsValid.ShouldBeTrue();
        new StripeWebhookVerifier(["whsec_ThirdSecretNobodyHasSignedWith000000"]).Verify(Signed(both), Arrival).IsValid.ShouldBeFalse();
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(300, true)]
    [InlineData(301, false)]
    [InlineData(-300, true)]
    [InlineData(-301, false)]
    public void The_timestamp_may_be_five_minutes_from_the_arrival_in_either_direction(int secondsAfterTimestamp, bool accepted)
    {
        var result = new StripeWebhookVerifier(Secret).Verify(Signed($"t={Timestamp},v1={Signature}"), Arrival.AddSeconds(secondsAfterTimestamp));

        result.IsValid.ShouldBe(accepted);
    }

    [Fact]
    public void The_tolerance_can_be_changed()
    {
        var header = $"t={Timestamp},v1={Signature}";

        new StripeWebhookVerifier(Secret, TimeSpan.FromMinutes(30)).Verify(Signed(header), Arrival.AddMinutes(30)).IsValid.ShouldBeTrue();
        new StripeWebhookVerifier(Secret, TimeSpan.FromMinutes(30)).Verify(Signed(header), Arrival.AddMinutes(31)).IsValid.ShouldBeFalse();
    }

    [Fact]
    public void A_replay_of_an_old_request_is_told_apart_from_a_forgery_in_the_log()
    {
        var verifier = new StripeWebhookVerifier(Secret);
        var stale = Arrival.AddDays(2);

        verifier.Verify(Signed($"t={Timestamp},v1={Signature}"), stale).Reason.ShouldNotBeNull().ShouldContain("tolerance");
        verifier.Verify(Signed($"t={Timestamp},v1={new string('0', 64)}"), stale).Reason.ShouldNotBeNull().ShouldContain("signature");
    }

    [Fact]
    public void The_timestamp_is_signed_so_changing_it_breaks_the_signature()
    {
        // Moving the timestamp forward is how a replay would try to look recent.
        var result = new StripeWebhookVerifier(Secret).Verify(Signed($"t={Timestamp + 3600},v1={Signature}"), Arrival.AddSeconds(3600));

        result.IsValid.ShouldBeFalse();
    }

    [Fact]
    public void A_body_that_was_changed_does_not_verify()
    {
        var verifier = new StripeWebhookVerifier(Secret);
        var header = $"t={Timestamp},v1={Signature}";

        verifier.Verify(Signed(header, Body.Replace("2000", "9000", StringComparison.Ordinal)), Arrival).IsValid.ShouldBeFalse();
        verifier.Verify(Signed(header, Body + "\n"), Arrival).IsValid.ShouldBeFalse();
        verifier.Verify(Signed(header, Body.Replace(",", ", ", StringComparison.Ordinal)), Arrival).IsValid.ShouldBeFalse();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("v1=4dc21d39494ce7358af90407e09cefa80fea054e96043dec4d5ee97d991dba45")]
    [InlineData("t=,v1=4dc21d39494ce7358af90407e09cefa80fea054e96043dec4d5ee97d991dba45")]
    [InlineData("t=abc,v1=4dc21d39494ce7358af90407e09cefa80fea054e96043dec4d5ee97d991dba45")]
    [InlineData("t=-1686089970,v1=4dc21d39494ce7358af90407e09cefa80fea054e96043dec4d5ee97d991dba45")]
    [InlineData("t=1686089970")]
    [InlineData("t=1686089970,v0=4dc21d39494ce7358af90407e09cefa80fea054e96043dec4d5ee97d991dba45")]
    [InlineData("t=1686089970,v2=4dc21d39494ce7358af90407e09cefa80fea054e96043dec4d5ee97d991dba45")]
    [InlineData("t=1686089970,v1=")]
    [InlineData("t=1686089970,v1=4dc21d39494ce7358af90407e09cefa80fea054e96043dec4d5ee97d991dba4")]
    [InlineData("t=1686089970,v1=4dc21d39494ce7358af90407e09cefa80fea054e96043dec4d5ee97d991dba455")]
    [InlineData("t=1686089970,v1=zzc21d39494ce7358af90407e09cefa80fea054e96043dec4d5ee97d991dba45")]
    [InlineData("t=1686089970,v1=4dc21d39494ce7358af90407e09cefa80fea054e96043dec4d5ee97d991dba45ff")]
    [InlineData("t=99999999999999999999,v1=4dc21d39494ce7358af90407e09cefa80fea054e96043dec4d5ee97d991dba45")]
    [InlineData("🙂")]
    public void A_header_that_is_missing_something_or_malformed_rejects_the_request_without_an_exception(string header)
    {
        new StripeWebhookVerifier(Secret).Verify(Signed(header), Arrival).IsValid.ShouldBeFalse();
    }

    [Fact]
    public void A_request_without_the_header_is_rejected()
    {
        var result = new StripeWebhookVerifier(Secret).Verify(new TestWebhookRequest(Body), Arrival);

        result.IsValid.ShouldBeFalse();
        result.Reason.ShouldNotBeNull().ShouldContain("missing");
    }

    [Fact]
    public void Constructing_with_a_bad_secret_fails_without_putting_it_in_the_message()
    {
        foreach (var secret in new[] { "", "   ", "\n" })
        {
            Should.Throw<ArgumentException>(() => new StripeWebhookVerifier(secret));
        }

        Should.Throw<ArgumentException>(() => new StripeWebhookVerifier(["whsec_ok", ""]));
        Should.Throw<ArgumentException>(() => new StripeWebhookVerifier(Array.Empty<string>()));
        Should.Throw<ArgumentException>(() => new StripeWebhookVerifier("whsec_\ud800lone-surrogate")).Message.ShouldNotContain("lone-surrogate");
        Should.Throw<ArgumentOutOfRangeException>(() => new StripeWebhookVerifier(Secret, TimeSpan.Zero));
    }
}
