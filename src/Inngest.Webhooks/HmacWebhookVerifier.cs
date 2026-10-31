// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.

using Inngest.Webhooks;

namespace Inngest;

/// <summary>
/// Verifies webhooks from any provider that signs with an HMAC of the body, described by <see cref="HmacWebhookOptions"/>:
/// the header, the hash, how the bytes are written and, for providers that sign one, a timestamp.
/// </summary>
/// <remarks>
/// A header can hold several signatures, and several secrets can be given, so that a secret can be replaced without
/// downtime: a request is accepted when any signature matches any of the secrets. Secrets are used as the text they are
/// written as. Without a <see cref="HmacWebhookOptions.TimestampHeader"/> nothing signed says when the request was sent,
/// so a captured request is accepted again for as long as the secret stays the same. If
/// <see cref="HmacWebhookOptions.DeliveryIdHeader"/> is set, its value becomes the id of the event, and Inngest drops a
/// second event with the same id for 24 hours; but the signed text does not include that header, so whoever holds one
/// captured request can send it again under another id. Without an id header the events have no id and nothing
/// deduplicates them.
/// </remarks>
public sealed class HmacWebhookVerifier : IWebhookVerifier
{
    private readonly HmacHeaderVerifier _verifier;

    /// <summary>Verifies with one secret.</summary>
    /// <param name="options">How the provider signs.</param>
    /// <param name="secret">The signing secret, as the provider shows it.</param>
    public HmacWebhookVerifier(HmacWebhookOptions options, string secret)
        : this(options, [secret])
    {
    }

    /// <summary>Verifies with several secrets, such as the new and the old one while a secret is replaced.</summary>
    /// <param name="options">How the provider signs.</param>
    /// <param name="secrets">The signing secrets, as the provider shows them.</param>
    public HmacWebhookVerifier(HmacWebhookOptions options, IEnumerable<string> secrets)
    {
        ArgumentNullException.ThrowIfNull(options);
        _verifier = new HmacHeaderVerifier(options, WebhookCrypto.TextKeys(secrets, nameof(secrets)));
    }

    /// <inheritdoc />
    public WebhookVerification Verify(IWebhookRequest request, DateTimeOffset now) => _verifier.Verify(request, now);
}
