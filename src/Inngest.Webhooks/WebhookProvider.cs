// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.

using Inngest.Webhooks;

namespace Inngest;

/// <summary>
/// A source of webhooks: how to tell that a request is from it, and how its deliveries become events. The factories
/// cover Stripe, GitHub, Shopify and providers that follow Standard Webhooks, or sign with an HMAC; the constructor
/// takes any other provider.
/// </summary>
/// <remarks>
/// Where the provider gives each delivery an id, the event gets it as its id. The id is the same for every attempt to
/// deliver one event, so Inngest starts no new runs for the attempts after the first, for as long as it keeps event ids
/// (24 hours). A delivery of such a provider that comes without its id is not an event and is answered with 400, since an
/// event without one would never be deduplicated. Only the ids of Standard Webhooks and Stripe are signed; GitHub and
/// Shopify send theirs, and the name of the event, in headers that their signatures do not cover.
/// </remarks>
public sealed class WebhookProvider
{
    /// <summary>A provider with a verifier of your own, and the code that turns a verified delivery into an event.</summary>
    /// <param name="name">What the provider is called in logs, such as <c>stripe</c>.</param>
    /// <param name="verifier">Tells whether a request is from the provider.</param>
    /// <param name="createEvent">
    /// Makes the event of a verified delivery, with <see cref="WebhookDelivery.DeliveryId"/> as its id. Returns null for a
    /// delivery that cannot be an event, which is answered with 400. The event may refer to the payload of the delivery:
    /// it is copied before it is sent.
    /// </param>
    public WebhookProvider(string name, IWebhookVerifier verifier, Func<WebhookDelivery, InngestEvent?> createEvent)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(verifier);
        ArgumentNullException.ThrowIfNull(createEvent);
        Name = name;
        Verifier = verifier;
        CreateEvent = createEvent;
    }

    // What the provider is called in logs.
    internal string Name { get; }

    // Tells whether a request is from the provider.
    internal IWebhookVerifier Verifier { get; }

    // Makes the event of a verified delivery.
    internal Func<WebhookDelivery, InngestEvent?> CreateEvent { get; }

    /// <summary>
    /// Stripe. The event is named <c>stripe/&lt;type&gt;</c>, such as <c>stripe/payment_intent.succeeded</c>, its data is
    /// the whole Stripe event, and its id is the id of the Stripe event (<c>evt_...</c>). All three are in the signed body.
    /// </summary>
    /// <param name="secret">The signing secret of the webhook endpoint (<c>whsec_...</c>).</param>
    /// <param name="tolerance">How far the signed timestamp may be from now. Five minutes when null.</param>
    public static WebhookProvider Stripe(string secret, TimeSpan? tolerance = null) =>
        new("stripe", new StripeWebhookVerifier(secret, tolerance), WebhookEvents.Stripe);

    /// <summary>Stripe, with several signing secrets, such as the new and the old one while a secret is rolled.</summary>
    /// <param name="secrets">The signing secrets of the webhook endpoint.</param>
    /// <param name="tolerance">How far the signed timestamp may be from now. Five minutes when null.</param>
    public static WebhookProvider Stripe(IEnumerable<string> secrets, TimeSpan? tolerance = null) =>
        new("stripe", new StripeWebhookVerifier(secrets, tolerance), WebhookEvents.Stripe);

    /// <summary>
    /// GitHub. The event is named <c>github/&lt;event&gt;</c>, such as <c>github/pull_request</c>, from the
    /// <c>X-GitHub-Event</c> header; its data is the payload and its id is <c>X-GitHub-Delivery</c>, which a delivery
    /// without is answered with 400. The signature covers the body only, so the name and the id are unsigned: someone who
    /// holds one captured delivery can send it again under another event name or id. The webhook must send
    /// <c>application/json</c>.
    /// </summary>
    /// <param name="secret">The secret of the webhook.</param>
    public static WebhookProvider GitHub(string secret) =>
        new("github", new GitHubWebhookVerifier(secret), WebhookEvents.GitHub);

    /// <summary>GitHub, with several secrets, such as the new and the old one while a secret is replaced.</summary>
    /// <param name="secrets">The secrets of the webhook.</param>
    public static WebhookProvider GitHub(IEnumerable<string> secrets) =>
        new("github", new GitHubWebhookVerifier(secrets), WebhookEvents.GitHub);

    /// <summary>
    /// Shopify. The event is named <c>shopify/&lt;topic&gt;</c> with the slash of the topic as a dot, such as
    /// <c>shopify/orders.create</c>, from <c>X-Shopify-Topic</c>; its data is the payload and its id is
    /// <c>X-Shopify-Webhook-Id</c>, which a delivery without is answered with 400. The signature covers the body only, so
    /// the name and the id are unsigned, as is <c>X-Shopify-Shop-Domain</c>, which is not put in the data: someone who
    /// holds one captured delivery can send it again under another topic, id or shop.
    /// </summary>
    /// <param name="secret">The client secret of the app.</param>
    public static WebhookProvider Shopify(string secret) =>
        new("shopify", new ShopifyWebhookVerifier(secret), WebhookEvents.Shopify);

    /// <summary>Shopify, with several client secrets, such as the new and the old one while the secret is rotated.</summary>
    /// <param name="secrets">The client secrets of the app.</param>
    public static WebhookProvider Shopify(IEnumerable<string> secrets) =>
        new("shopify", new ShopifyWebhookVerifier(secrets), WebhookEvents.Shopify);

    /// <summary>
    /// A provider that follows Standard Webhooks (standardwebhooks.com), such as Svix, Clerk and Resend. The event is named
    /// <c>&lt;eventPrefix&gt;/&lt;type&gt;</c> from the <c>type</c> of the payload, such as <c>clerk/user.created</c>; its
    /// data is the <c>data</c> of the payload, and its id is <c>webhook-id</c> (<c>svix-id</c> for Svix). The id, the
    /// timestamp and the body are signed.
    /// </summary>
    /// <param name="eventPrefix">What the names of the events start with, before a slash, such as <c>clerk</c>.</param>
    /// <param name="secret">The signing secret of the endpoint (<c>whsec_...</c>).</param>
    /// <param name="tolerance">How far the signed timestamp may be from now. Five minutes when null.</param>
    public static WebhookProvider StandardWebhooks(string eventPrefix, string secret, TimeSpan? tolerance = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventPrefix);
        return new(eventPrefix, new StandardWebhooksVerifier(secret, tolerance), WebhookEvents.StandardWebhooks(eventPrefix));
    }

    /// <summary>A Standard Webhooks provider with several secrets, such as the new and the old one while a secret is replaced.</summary>
    /// <param name="eventPrefix">What the names of the events start with, before a slash, such as <c>clerk</c>.</param>
    /// <param name="secrets">The signing secrets of the endpoint (<c>whsec_...</c>).</param>
    /// <param name="tolerance">How far the signed timestamp may be from now. Five minutes when null.</param>
    public static WebhookProvider StandardWebhooks(string eventPrefix, IEnumerable<string> secrets, TimeSpan? tolerance = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventPrefix);
        return new(eventPrefix, new StandardWebhooksVerifier(secrets, tolerance), WebhookEvents.StandardWebhooks(eventPrefix));
    }

    /// <summary>
    /// A provider that signs the body with an HMAC. Every delivery becomes an event with the name given and the payload as
    /// its data. The event has an id only when <see cref="HmacWebhookOptions.DeliveryIdHeader"/> names the header that
    /// holds it, which a delivery must then have, or is answered with 400; without an id header nothing deduplicates the
    /// events. Unless the provider signs that header, the id can be changed by whoever holds one captured delivery. Use
    /// <see cref="WebhookOptions.Transform"/> to name events after their content.
    /// </summary>
    /// <param name="eventName">The name of every event, such as <c>acme/webhook.received</c>.</param>
    /// <param name="options">How the provider signs.</param>
    /// <param name="secret">The signing secret.</param>
    public static WebhookProvider Hmac(string eventName, HmacWebhookOptions options, string secret)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventName);
        var verifier = new HmacWebhookVerifier(options, secret);
        return new("hmac", verifier, WebhookEvents.Fixed(eventName, requireId: !string.IsNullOrWhiteSpace(options.DeliveryIdHeader)));
    }

    /// <summary>A provider that signs the body with an HMAC, with several secrets, such as the new and the old one while a secret is replaced.</summary>
    /// <param name="eventName">The name of every event, such as <c>acme/webhook.received</c>.</param>
    /// <param name="options">How the provider signs.</param>
    /// <param name="secrets">The signing secrets.</param>
    public static WebhookProvider Hmac(string eventName, HmacWebhookOptions options, IEnumerable<string> secrets)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventName);
        var verifier = new HmacWebhookVerifier(options, secrets);
        return new("hmac", verifier, WebhookEvents.Fixed(eventName, requireId: !string.IsNullOrWhiteSpace(options.DeliveryIdHeader)));
    }
}
