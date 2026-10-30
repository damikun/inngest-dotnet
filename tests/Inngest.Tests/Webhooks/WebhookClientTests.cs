using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Inngest.Tests.Client;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Inngest.Tests.Webhooks;

// The webhook endpoint with the real client of the SDK, whose HTTP calls are scripted, so that what reaches Inngest is
// checked as JSON: the payload is a JsonElement, which the client has to write through the serializer options of the app.
public sealed class WebhookClientTests
{
    private const string StripeSecret = "whsec_Hk3pX9wZq2LmN7vR4tYb8cDf6gJa1sE5";

    // The time of the client's fake clock, 2024-01-18T14:01:44Z, which is also the moment the webhook arrives.
    private static readonly DateTimeOffset Now = new(2024, 1, 18, 14, 1, 44, TimeSpan.Zero);

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_payload_reaches_Inngest_as_the_data_of_an_event_with_the_delivery_id_even_when_the_app_runs_without_reflection()
    {
        // What a Native AOT app configures: its own types and nothing else. The client adds what it needs itself.
        var fixture = Fixture(new JsonSerializerOptions { TypeInfoResolver = ClientTestJsonContext.Default });
        const string Body = """{"id":"evt_1NG8Du2eZvKYlo2CUI8iWOzQ","type":"payment_intent.succeeded","data":{"object":{"id":"pi_1","amount":2000,"big":820982911946154508}}}""";
        await using var app = await StartAsync(fixture, WebhookProvider.Stripe(StripeSecret));

        using var response = await PostStripe(app, Body);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        fixture.Handler.Single.BodyText.ShouldBe($$"""[{"name":"stripe/payment_intent.succeeded","data":{{Body}},"id":"evt_1NG8Du2eZvKYlo2CUI8iWOzQ","ts":1705586504000}]""");
    }

    [Fact]
    public async Task Text_the_encoder_escapes_arrives_as_the_same_json_data()
    {
        var fixture = Fixture(new JsonSerializerOptions { TypeInfoResolver = ClientTestJsonContext.Default });
        const string Body = """{"id":"evt_1","type":"customer.updated","data":{"name":"Zoë <b>&amp;</b> \"quoted\" 🙂\n\ttabbed","note":"a\u0000b"}}""";
        await using var app = await StartAsync(fixture, WebhookProvider.Stripe(StripeSecret));

        using var response = await PostStripe(app, Body);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var sent = JsonNode.Parse(fixture.Handler.Single.BodyText)!.AsArray().Single()!;
        JsonNode.DeepEquals(sent["data"], JsonNode.Parse(Body)).ShouldBeTrue();
    }

    [Fact]
    public async Task A_shopify_payload_with_the_shop_added_is_written_like_any_other()
    {
        var fixture = Fixture(new JsonSerializerOptions { TypeInfoResolver = ClientTestJsonContext.Default });
        const string Body = """{"id":820982911946154508,"total_price":"403.00"}""";
        await using var app = await StartAsync(fixture, WebhookProvider.Shopify("shpss_secret"));
        using var request = new HttpRequestMessage(HttpMethod.Post, "/webhook")
        {
            Content = new StringContent(Body, Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("X-Shopify-Hmac-Sha256", Convert.ToBase64String(HMACSHA256.HashData(Encoding.UTF8.GetBytes("shpss_secret"), Encoding.UTF8.GetBytes(Body))));
        request.Headers.Add("X-Shopify-Topic", "orders/create");
        request.Headers.Add("X-Shopify-Shop-Domain", "quickstart-0a1b2c3d.myshopify.com");
        request.Headers.Add("X-Shopify-Webhook-Id", "b54557e4-bdd9-4b37-8a5f-bf7d70bcd043");

        using var response = await app.GetTestClient().SendAsync(request, Cancellation);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        fixture.Handler.Single.BodyText.ShouldBe(
            """[{"name":"shopify/orders.create","data":{"id":820982911946154508,"total_price":"403.00","shop_domain":"quickstart-0a1b2c3d.myshopify.com"},"id":"b54557e4-bdd9-4b37-8a5f-bf7d70bcd043","ts":1705586504000}]""");
    }

    [Fact]
    public async Task A_transform_can_send_data_of_a_type_the_app_serializes_itself()
    {
        var fixture = Fixture(new JsonSerializerOptions(JsonSerializerDefaults.Web) { TypeInfoResolver = ClientTestJsonContext.Default });
        await using var app = await StartAsync(
            fixture,
            WebhookProvider.Stripe(StripeSecret),
            new WebhookOptions { Transform = (_, @event) => @event with { Name = "shop/order.placed", Data = new OrderPlaced("o-1", 3) } });

        using var response = await PostStripe(app, """{"id":"evt_1","type":"checkout.session.completed"}""");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        fixture.Handler.Single.BodyText.ShouldBe("""[{"name":"shop/order.placed","data":{"orderId":"o-1","quantity":3},"id":"evt_1","ts":1705586504000}]""");
    }

    [Fact]
    public async Task An_event_name_Inngest_reserves_is_a_bad_request_and_is_not_sent()
    {
        var fixture = Fixture(new JsonSerializerOptions { TypeInfoResolver = ClientTestJsonContext.Default });
        await using var app = await StartAsync(
            fixture,
            WebhookProvider.Stripe(StripeSecret),
            new WebhookOptions { Transform = (_, @event) => @event with { Name = "inngest/function.finished" } });

        using var response = await PostStripe(app, """{"id":"evt_1","type":"ping"}""");

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        fixture.Handler.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_payload_that_is_not_an_object_cannot_be_the_data_of_an_event_and_is_a_bad_request()
    {
        var fixture = Fixture(new JsonSerializerOptions { TypeInfoResolver = ClientTestJsonContext.Default });
        var provider = WebhookProvider.Hmac("acme/webhook", new HmacWebhookOptions { SignatureHeader = "X-Signature" }, "secret");
        await using var app = await StartAsync(fixture, provider);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/webhook") { Content = new StringContent("[1,2,3]", Encoding.UTF8, "application/json") };
        request.Headers.Add("X-Signature", Signing.Hex(HMACSHA256.HashData(Encoding.UTF8.GetBytes("secret"), "[1,2,3]"u8)));

        using var response = await app.GetTestClient().SendAsync(request, Cancellation);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        fixture.Handler.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task Inngest_refusing_the_event_is_a_503_and_the_provider_delivers_it_again()
    {
        var fixture = Fixture(new JsonSerializerOptions { TypeInfoResolver = ClientTestJsonContext.Default }, accept: false);
        fixture.Handler.Respond(HttpStatusCode.RequestEntityTooLarge, """{"status":413,"error":"Event payload too large"}""");
        await using var app = await StartAsync(fixture, WebhookProvider.Stripe(StripeSecret));

        using var response = await PostStripe(app, """{"id":"evt_1","type":"ping"}""");

        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
    }

    // Answers the first send as Inngest does when it accepts the events, unless the test scripts the answer.
    private static ClientFixture Fixture(JsonSerializerOptions serializerOptions, bool accept = true)
    {
        var fixture = new ClientFixture(TestSettings.Create(options => options.SerializerOptions = serializerOptions));
        if (accept)
        {
            fixture.Handler.Respond(HttpStatusCode.OK, ClientFixture.Accepted);
        }

        return fixture;
    }

    private static async Task<WebApplication> StartAsync(ClientFixture fixture, WebhookProvider provider, WebhookOptions? options = null)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton<IInngestClient>(fixture.Client);
        builder.Services.AddSingleton<TimeProvider>(fixture.Time);
        var app = builder.Build();
        app.MapInngestWebhook("/webhook", provider, options);
        await app.StartAsync(Cancellation);
        return app;
    }

    private static async Task<HttpResponseMessage> PostStripe(WebApplication app, string body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/webhook") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        request.Headers.Add("Stripe-Signature", Signing.StripeHeader(StripeSecret, Now.ToUnixTimeSeconds(), Encoding.UTF8.GetBytes(body)));
        return await app.GetTestClient().SendAsync(request, Cancellation);
    }
}
