namespace Inngest.Tests.Webhooks;

public sealed class ShopifyWebhookVerifierTests
{
    // Signed by hand with Python's hmac: the base64 HMAC-SHA256 of the raw body, keyed with the client secret, as
    // https://shopify.dev/docs/apps/build/webhooks/subscribe/https describes.
    private const string Secret = "test-shopify-client-secret";
    private const string OldSecret = "test-shopify-client-secret-before-rotation";
    private const string Signature = "4SB0akUKJed6I9/utLPXALirCS4MU+QPTmysnEScE58=";
    private const string OldSignature = "PfsNfjqUAlDhOttF/RGQK56zaM7yxJajAKpn+Ymli8Q=";
    private const string Body = """{"id":820982911946154508,"admin_graphql_api_id":"gid://shopify/Order/820982911946154508","email":"jon@example.com","total_price":"403.00","currency":"USD"}""";

    private static readonly DateTimeOffset Now = new(2026, 10, 29, 12, 0, 0, TimeSpan.Zero);

    private static TestWebhookRequest Signed(string signature = Signature, string body = Body) =>
        new TestWebhookRequest(body).With("X-Shopify-Hmac-Sha256", signature);

    [Fact]
    public void The_signature_the_algorithm_gives_verifies()
    {
        new ShopifyWebhookVerifier(Secret).Verify(Signed(), Now).IsValid.ShouldBeTrue();
    }

    [Fact]
    public void The_delivery_id_is_the_webhook_id_and_not_the_event_id_that_subscriptions_share()
    {
        var request = Signed()
            .With("X-Shopify-Webhook-Id", "b54557e4-bdd9-4b37-8a5f-bf7d70bcd043")
            .With("X-Shopify-Event-Id", "98880d05-33a2-40d7-bf9f-b9a0cd2b3210");

        new ShopifyWebhookVerifier(Secret).Verify(request, Now).DeliveryId.ShouldBe("b54557e4-bdd9-4b37-8a5f-bf7d70bcd043");
    }

    [Fact]
    public void The_time_of_arrival_does_not_matter_because_nothing_signed_says_when_it_was_sent()
    {
        // X-Shopify-Triggered-At is not covered by the signature, so it is not trusted.
        var request = Signed().With("X-Shopify-Triggered-At", "2020-01-01T00:00:00Z");

        new ShopifyWebhookVerifier(Secret).Verify(request, Now.AddYears(2)).IsValid.ShouldBeTrue();
    }

    [Fact]
    public void The_new_and_the_old_client_secret_both_pass_while_Shopify_changes_over()
    {
        var verifier = new ShopifyWebhookVerifier([Secret, OldSecret]);

        verifier.Verify(Signed(), Now).IsValid.ShouldBeTrue();
        verifier.Verify(Signed(OldSignature), Now).IsValid.ShouldBeTrue();
        new ShopifyWebhookVerifier(Secret).Verify(Signed(OldSignature), Now).IsValid.ShouldBeFalse();
    }

    [Fact]
    public void A_changed_body_does_not_verify()
    {
        var verifier = new ShopifyWebhookVerifier(Secret);

        verifier.Verify(Signed(body: Body.Replace("403.00", "1.00", StringComparison.Ordinal)), Now).IsValid.ShouldBeFalse();
        verifier.Verify(Signed(body: Body + " "), Now).IsValid.ShouldBeFalse();
    }

    [Theory]
    [InlineData("")]
    [InlineData("4SB0akUKJed6I9/utLPXALirCS4MU+QPTmysnEScE58")]
    [InlineData("4SB0akUKJed6I9/utLPXALirCS4MU+QPTmysnEScE5")]
    [InlineData("4SB0akUKJed6I9/utLPXALirCS4MU+QPTmysnEScE58==")]
    [InlineData("!!!!")]
    [InlineData("4SB0akUKJed6I9/utLPXALirCS4MU+QPTmysnEScE584SB0akUKJed6I9/utLPXALirCS4MU+QPTmysnEScE58=")]
    [InlineData("8821f1d06b73 74db0c02e7a7eac2662 8c96520c38eb36")]
    [InlineData("🙂")]
    public void A_signature_that_is_not_base64_of_a_digest_is_rejected_without_an_exception(string header)
    {
        new ShopifyWebhookVerifier(Secret).Verify(Signed(header), Now).IsValid.ShouldBeFalse();
    }

    [Fact]
    public void A_request_without_the_header_is_rejected()
    {
        new ShopifyWebhookVerifier(Secret).Verify(new TestWebhookRequest(Body), Now).IsValid.ShouldBeFalse();
    }
}
