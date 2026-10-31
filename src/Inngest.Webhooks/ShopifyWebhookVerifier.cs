// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.

using Inngest.Webhooks;

namespace Inngest;

/// <summary>
/// Verifies Shopify webhooks. The <c>X-Shopify-Hmac-Sha256</c> header is the base64 HMAC-SHA256 of the body, keyed with
/// the app's client secret.
/// </summary>
/// <remarks>
/// Shopify signs the body and nothing else. A delivery has no signed timestamp, and no header is covered by the
/// signature: not <c>X-Shopify-Triggered-At</c>, not <c>X-Shopify-Topic</c>, which names the event, not
/// <c>X-Shopify-Webhook-Id</c>, which becomes its id, and not <c>X-Shopify-Shop-Domain</c>. Anyone who holds one captured
/// delivery can send it again under another topic, id or shop. After the client secret is rotated the signatures can take
/// up to an hour to change, so give both secrets for that time.
/// </remarks>
public sealed class ShopifyWebhookVerifier : IWebhookVerifier
{
    private static readonly HmacWebhookOptions Options = new()
    {
        SignatureHeader = "X-Shopify-Hmac-Sha256",
        SignatureEncoding = HmacSignatureEncoding.Base64,
        DeliveryIdHeader = "X-Shopify-Webhook-Id",
    };

    private readonly HmacHeaderVerifier _verifier;

    /// <summary>Verifies with the client secret of the app.</summary>
    /// <param name="secret">The client secret of the app, as Shopify shows it.</param>
    public ShopifyWebhookVerifier(string secret)
        : this([secret])
    {
    }

    /// <summary>Verifies with several client secrets, such as the new and the old one while the secret is rotated.</summary>
    /// <param name="secrets">The client secrets of the app, as Shopify shows them.</param>
    public ShopifyWebhookVerifier(IEnumerable<string> secrets)
    {
        _verifier = new HmacHeaderVerifier(Options, WebhookCrypto.TextKeys(secrets, nameof(secrets)));
    }

    /// <inheritdoc />
    public WebhookVerification Verify(IWebhookRequest request, DateTimeOffset now) => _verifier.Verify(request, now);
}
