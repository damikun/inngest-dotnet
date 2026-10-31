using System.Text.Json;

namespace Inngest.Tests.Webhooks;

// How each provider's deliveries become events, which follows the transforms in Inngest's guide to webhooks.
public sealed class WebhookEventTests
{
    private const string StripeEvent = """{"id":"evt_1NG8Du2eZvKYlo2CUI8iWOzQ","object":"event","type":"payment_intent.succeeded","data":{"object":{"id":"pi_3NG8Dt2eZvKYlo2C0jbDz8Qa","amount":2000}}}""";

    private static WebhookDelivery Delivery(string json, string? deliveryId = null, Action<TestWebhookRequest>? headers = null)
    {
        var request = new TestWebhookRequest(json);
        headers?.Invoke(request);
        return new WebhookDelivery("test", deliveryId, JsonDocument.Parse(json).RootElement, request);
    }

    private static string DataJson(InngestEvent? @event) => ((JsonElement)@event!.Data!).GetRawText();

    [Fact]
    public void A_Stripe_event_is_named_after_its_type_carries_the_whole_event_and_has_the_id_of_the_Stripe_event()
    {
        var @event = WebhookProvider.Stripe("whsec_x").CreateEvent(Delivery(StripeEvent));

        @event.ShouldNotBeNull();
        @event.Name.ShouldBe("stripe/payment_intent.succeeded");
        @event.Id.ShouldBe("evt_1NG8Du2eZvKYlo2CUI8iWOzQ");
        DataJson(@event).ShouldBe(StripeEvent);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("\"text\"")]
    [InlineData("42")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("""{"id":"evt_1","type":7}""")]
    [InlineData("""{"id":"evt_1","type":""}""")]
    [InlineData("""{"id":"evt_1","type":"has space"}""")]
    [InlineData("""{"id":"evt_1","type":"new\nline"}""")]
    [InlineData("""{"id":"evt_1","type":"mésg"}""")]
    public void A_Stripe_payload_without_a_usable_type_is_not_an_event(string json)
    {
        WebhookProvider.Stripe("whsec_x").CreateEvent(Delivery(json)).ShouldBeNull();
    }

    [Theory]
    [InlineData("""{"type":"ping"}""")]
    [InlineData("""{"id":"","type":"ping"}""")]
    [InlineData("""{"id":"two words","type":"ping"}""")]
    [InlineData("""{"id":5,"type":"ping"}""")]
    public void A_Stripe_event_without_a_usable_id_is_not_an_event_because_it_would_never_be_deduplicated(string json)
    {
        WebhookProvider.Stripe("whsec_x").CreateEvent(Delivery(json)).ShouldBeNull();
    }

    [Fact]
    public void An_event_name_is_never_longer_than_Inngest_takes_whatever_the_prefix_adds_to_the_type()
    {
        var provider = WebhookProvider.Stripe("whsec_x");

        // "stripe/" is seven characters: 249 of type make exactly 256, and 250 make 257.
        provider.CreateEvent(Delivery($$"""{"id":"evt_1","type":"{{new string('a', 249)}}"}""")).ShouldNotBeNull().Name.Length.ShouldBe(256);
        provider.CreateEvent(Delivery($$"""{"id":"evt_1","type":"{{new string('a', 250)}}"}""")).ShouldBeNull();
        provider.CreateEvent(Delivery($$"""{"id":"evt_1","type":"{{new string('a', 257)}}"}""")).ShouldBeNull();
    }

    [Fact]
    public void A_Standard_Webhooks_event_is_named_with_the_prefix_and_carries_the_data_of_the_payload()
    {
        var provider = WebhookProvider.StandardWebhooks("clerk", "whsec_MfKQ9r8GKYqrTwjUPD8ILPZIo2LaLaSw");
        var json = """{"type":"user.created","timestamp":"2022-11-03T20:26:10.344522Z","data":{"id":"user_29w83sxmDNGwOuEthce5gg56FcC","first_name":"Ada"}}""";

        var @event = provider.CreateEvent(Delivery(json, deliveryId: "msg_2KWPBgLlAfxdpx2AI54pPJ85f4W"));

        @event.ShouldNotBeNull();
        @event.Name.ShouldBe("clerk/user.created");
        @event.Id.ShouldBe("msg_2KWPBgLlAfxdpx2AI54pPJ85f4W");
        DataJson(@event).ShouldBe("""{"id":"user_29w83sxmDNGwOuEthce5gg56FcC","first_name":"Ada"}""");
        provider.Name.ShouldBe("clerk");
    }

    [Theory]
    [InlineData("""{"type":"ping"}""")]
    [InlineData("""{"type":"ping","data":[1,2]}""")]
    [InlineData("""{"type":"ping","data":"text"}""")]
    [InlineData("""{"type":"ping","data":null}""")]
    public void A_Standard_Webhooks_payload_without_an_object_as_data_is_the_data_itself(string json)
    {
        var provider = WebhookProvider.StandardWebhooks("resend", "whsec_MfKQ9r8GKYqrTwjUPD8ILPZIo2LaLaSw");

        DataJson(provider.CreateEvent(Delivery(json, "msg_1"))).ShouldBe(json);
    }

    [Theory]
    [InlineData("""{"data":{}}""")]
    [InlineData("""{"type":3,"data":{}}""")]
    [InlineData("[]")]
    public void A_Standard_Webhooks_payload_without_a_type_is_not_an_event(string json)
    {
        WebhookProvider.StandardWebhooks("resend", "whsec_MfKQ9r8GKYqrTwjUPD8ILPZIo2LaLaSw").CreateEvent(Delivery(json, "msg_1")).ShouldBeNull();
    }

    [Fact]
    public void A_Standard_Webhooks_delivery_without_an_id_is_not_an_event()
    {
        WebhookProvider.StandardWebhooks("resend", "whsec_MfKQ9r8GKYqrTwjUPD8ILPZIo2LaLaSw").CreateEvent(Delivery("""{"type":"ping"}""")).ShouldBeNull();
    }

    [Fact]
    public void A_GitHub_event_is_named_after_the_event_header_and_has_the_delivery_id()
    {
        var json = """{"action":"opened","number":7}""";

        var @event = WebhookProvider.GitHub("secret").CreateEvent(Delivery(json, "72d3162e-cc78-11e3-81ab-4c9367dc0958", request => request.With("X-GitHub-Event", "pull_request")));

        @event.ShouldNotBeNull();
        @event.Name.ShouldBe("github/pull_request");
        @event.Id.ShouldBe("72d3162e-cc78-11e3-81ab-4c9367dc0958");
        DataJson(@event).ShouldBe(json);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("pull request")]
    [InlineData("ünïcode")]
    public void A_GitHub_delivery_without_a_usable_event_header_is_not_an_event(string? header)
    {
        var request = header is null ? (Action<TestWebhookRequest>?)null : r => r.With("X-GitHub-Event", header);

        WebhookProvider.GitHub("secret").CreateEvent(Delivery("""{"zen":"Keep it logically awesome."}""", "id", request)).ShouldBeNull();
    }

    [Fact]
    public void A_GitHub_delivery_without_an_id_is_not_an_event_because_it_would_never_be_deduplicated()
    {
        WebhookProvider.GitHub("secret").CreateEvent(Delivery("""{"zen":"x"}""", deliveryId: null, request => request.With("X-GitHub-Event", "ping"))).ShouldBeNull();
    }

    [Fact]
    public void A_GitHub_payload_that_is_not_an_object_is_not_an_event()
    {
        WebhookProvider.GitHub("secret").CreateEvent(Delivery("[1]", "id", request => request.With("X-GitHub-Event", "push"))).ShouldBeNull();
    }

    [Fact]
    public void A_GitHub_event_name_is_never_longer_than_Inngest_takes()
    {
        var provider = WebhookProvider.GitHub("secret");

        // "github/" is seven characters.
        provider.CreateEvent(Delivery("{}", "id", request => request.With("X-GitHub-Event", new string('e', 249)))).ShouldNotBeNull().Name.Length.ShouldBe(256);
        provider.CreateEvent(Delivery("{}", "id", request => request.With("X-GitHub-Event", new string('e', 250)))).ShouldBeNull();
    }

    [Theory]
    [InlineData("orders/create", "shopify/orders.create")]
    [InlineData("products/update", "shopify/products.update")]
    [InlineData("app/uninstalled", "shopify/app.uninstalled")]
    [InlineData("customers/data_request", "shopify/customers.data_request")]
    [InlineData("a/b/c", "shopify/a.b.c")]
    public void A_Shopify_event_is_named_after_the_topic_with_dots_for_slashes(string topic, string name)
    {
        var @event = WebhookProvider.Shopify("secret").CreateEvent(Delivery("""{"id":1}""", "b54557e4-bdd9-4b37-8a5f-bf7d70bcd043", request => request.With("X-Shopify-Topic", topic)));

        @event.ShouldNotBeNull();
        @event.Name.ShouldBe(name);
        @event.Id.ShouldBe("b54557e4-bdd9-4b37-8a5f-bf7d70bcd043");
    }

    [Fact]
    public void A_Shopify_event_carries_the_payload_and_nothing_from_the_headers_because_only_the_body_is_signed()
    {
        var payload = """{"id":820982911946154508,"total_price":"403.00"}""";

        var @event = WebhookProvider.Shopify("secret").CreateEvent(Delivery(
            payload,
            "w-1",
            request => request
                .With("X-Shopify-Topic", "orders/create")
                .With("X-Shopify-Shop-Domain", "quickstart-0a1b2c3d.myshopify.com")
                .With("X-Shopify-API-Version", "2025-01")));

        DataJson(@event).ShouldBe(payload);
    }

    [Fact]
    public void A_Shopify_delivery_without_an_id_is_not_an_event_because_it_would_never_be_deduplicated()
    {
        WebhookProvider.Shopify("secret").CreateEvent(Delivery("""{"id":1}""", deliveryId: null, request => request.With("X-Shopify-Topic", "orders/create"))).ShouldBeNull();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("two words")]
    public void A_Shopify_delivery_without_a_usable_topic_is_not_an_event(string? topic)
    {
        var request = topic is null ? (Action<TestWebhookRequest>?)null : r => r.With("X-Shopify-Topic", topic);

        WebhookProvider.Shopify("secret").CreateEvent(Delivery("""{"id":1}""", "w-1", request)).ShouldBeNull();
    }

    [Fact]
    public void A_generic_HMAC_provider_names_every_event_the_same_and_uses_the_delivery_id_header()
    {
        var options = new HmacWebhookOptions { SignatureHeader = "X-Signature", DeliveryIdHeader = "X-Delivery" };
        var provider = WebhookProvider.Hmac("acme/webhook.received", options, "secret");

        var @event = provider.CreateEvent(Delivery("""{"kind":"invoice"}""", "d-9"));

        @event.ShouldNotBeNull();
        @event.Name.ShouldBe("acme/webhook.received");
        @event.Id.ShouldBe("d-9");
        provider.CreateEvent(Delivery("[1]", "d-9")).ShouldBeNull();
    }

    [Fact]
    public void A_generic_HMAC_provider_that_names_an_id_header_needs_the_id_and_one_that_does_not_sends_events_without_one()
    {
        var withHeader = WebhookProvider.Hmac("acme/webhook.received", new HmacWebhookOptions { SignatureHeader = "X-Signature", DeliveryIdHeader = "X-Delivery" }, "secret");
        var without = WebhookProvider.Hmac("acme/webhook.received", new HmacWebhookOptions { SignatureHeader = "X-Signature" }, "secret");

        withHeader.CreateEvent(Delivery("{}", deliveryId: null)).ShouldBeNull();
        without.CreateEvent(Delivery("{}", deliveryId: null)).ShouldNotBeNull().Id.ShouldBeNull();
    }

    [Fact]
    public void A_provider_checks_what_it_is_given()
    {
        var verifier = new GitHubWebhookVerifier("secret");

        Should.Throw<ArgumentException>(() => new WebhookProvider(" ", verifier, _ => null));
        Should.Throw<ArgumentNullException>(() => new WebhookProvider("x", null!, _ => null));
        Should.Throw<ArgumentNullException>(() => new WebhookProvider("x", verifier, null!));
        Should.Throw<ArgumentException>(() => WebhookProvider.StandardWebhooks("", "whsec_MfKQ9r8GKYqrTwjUPD8ILPZIo2LaLaSw"));
        Should.Throw<ArgumentException>(() => WebhookProvider.Hmac("", new HmacWebhookOptions { SignatureHeader = "X" }, "secret"));
        Should.Throw<ArgumentException>(() => WebhookProvider.Stripe(""));
    }

    [Fact]
    public void A_delivery_can_be_made_in_a_test_with_or_without_a_request()
    {
        var payload = JsonDocument.Parse("""{"id":1}""").RootElement;
        var request = new TestWebhookRequest("{}").With("X-Shopify-Shop-Domain", "a.myshopify.com");

        var bare = new WebhookDelivery("shopify", "w-1", payload);
        var withRequest = new WebhookDelivery("shopify", "w-1", payload, request);

        bare.GetHeader("X-Shopify-Shop-Domain").ShouldBeNull();
        bare.Services.ShouldBeNull();
        withRequest.GetHeader("x-shopify-shop-domain").ShouldBe("a.myshopify.com");
        withRequest.Provider.ShouldBe("shopify");
        withRequest.DeliveryId.ShouldBe("w-1");
        withRequest.Payload.GetProperty("id").GetInt32().ShouldBe(1);
        Should.Throw<ArgumentException>(() => new WebhookDelivery(" ", null, payload));
    }
}
