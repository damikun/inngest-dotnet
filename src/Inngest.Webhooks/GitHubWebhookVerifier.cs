// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.

using Inngest.Webhooks;

namespace Inngest;

/// <summary>
/// Verifies GitHub webhooks. The <c>X-Hub-Signature-256</c> header is <c>sha256=</c> and the hexadecimal HMAC-SHA256 of
/// the body, keyed with the webhook's secret. The older <c>X-Hub-Signature</c>, which uses SHA-1, is ignored.
/// </summary>
/// <remarks>
/// GitHub signs the body and nothing else. A delivery has no signed timestamp, so a captured request verifies for as
/// long as the secret stays the same. The headers are not covered by the signature: <c>X-GitHub-Event</c>, which names
/// the event, and <c>X-GitHub-Delivery</c>, which becomes its id, can be changed by anyone who holds one captured
/// delivery, who can then send it again under another event name or id. Several secrets can be given so that a secret can
/// be replaced without downtime.
/// </remarks>
public sealed class GitHubWebhookVerifier : IWebhookVerifier
{
    private static readonly HmacWebhookOptions Options = new()
    {
        SignatureHeader = "X-Hub-Signature-256",
        SignaturePrefix = "sha256=",
        DeliveryIdHeader = "X-GitHub-Delivery",
    };

    private readonly HmacHeaderVerifier _verifier;

    /// <summary>Verifies with the secret of the webhook.</summary>
    /// <param name="secret">The secret as it was entered on GitHub.</param>
    public GitHubWebhookVerifier(string secret)
        : this([secret])
    {
    }

    /// <summary>Verifies with several secrets, such as the new and the old one while a secret is replaced.</summary>
    /// <param name="secrets">The secrets as they were entered on GitHub.</param>
    public GitHubWebhookVerifier(IEnumerable<string> secrets)
    {
        _verifier = new HmacHeaderVerifier(Options, WebhookCrypto.TextKeys(secrets, nameof(secrets)));
    }

    /// <inheritdoc />
    public WebhookVerification Verify(IWebhookRequest request, DateTimeOffset now) => _verifier.Verify(request, now);
}
