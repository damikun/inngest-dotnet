// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.

using System.Security.Cryptography;
using Inngest.Webhooks;

namespace Inngest;

/// <summary>
/// Verifies Stripe webhooks. The <c>Stripe-Signature</c> header is <c>t=&lt;unix seconds&gt;,v1=&lt;hex&gt;</c>, where
/// <c>v1</c> is the HMAC-SHA256 of <c>{t}.{body}</c> keyed with the endpoint's signing secret, used as the text it is
/// written as (<c>whsec_...</c>, not decoded).
/// </summary>
/// <remarks>
/// When a secret is rolled in the Stripe dashboard and the old one stays valid for up to 24 hours, the header has one
/// <c>v1</c> signature for each. Pass both secrets, and a request is accepted when any signature matches any of them.
/// Other schemes, such as the <c>v0</c> that Stripe sends for test events, are ignored. A timestamp more than the
/// tolerance from the time of arrival, in either direction, rejects the request, after its signature has been checked.
/// </remarks>
public sealed class StripeWebhookVerifier : IWebhookVerifier
{
    private const string HeaderName = "Stripe-Signature";

    private readonly byte[][] _keys;
    private readonly TimeSpan _tolerance;

    /// <summary>Verifies with one signing secret, the <c>whsec_...</c> of the webhook endpoint.</summary>
    /// <param name="secret">The signing secret, exactly as Stripe shows it.</param>
    /// <param name="tolerance">How far the timestamp may be from now. Five minutes when null, as in Stripe's libraries.</param>
    public StripeWebhookVerifier(string secret, TimeSpan? tolerance = null)
        : this([secret], tolerance)
    {
    }

    /// <summary>Verifies with several signing secrets, such as the new and the old one while a secret is rolled.</summary>
    /// <param name="secrets">The signing secrets, exactly as Stripe shows them.</param>
    /// <param name="tolerance">How far the timestamp may be from now. Five minutes when null, as in Stripe's libraries.</param>
    public StripeWebhookVerifier(IEnumerable<string> secrets, TimeSpan? tolerance = null)
    {
        _keys = WebhookCrypto.TextKeys(secrets, nameof(secrets));
        _tolerance = WebhookCrypto.Tolerance(tolerance, nameof(tolerance));
    }

    /// <inheritdoc />
    public WebhookVerification Verify(IWebhookRequest request, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(request);
        var header = WebhookCrypto.Header(request, HeaderName);
        if (header is null)
        {
            return WebhookVerification.Invalid(WebhookRejection.MissingHeaders);
        }

        // The header is a list of name=value pairs. The first t is the timestamp, and every v1 is a signature.
        var timestamp = default(ReadOnlySpan<char>);
        var remaining = header.AsSpan();
        ReadOnlySpan<char> item;
        while (WebhookCrypto.TryTakeToken(ref remaining, ",", out item))
        {
            item = item.Trim();
            if (item.StartsWith("t=", StringComparison.Ordinal))
            {
                timestamp = item[2..].Trim();
                break;
            }
        }

        if (!WebhookCrypto.TryParseTimestamp(timestamp, out var seconds))
        {
            return WebhookVerification.Invalid(WebhookRejection.MalformedHeaders);
        }

        Span<byte> prefix = stackalloc byte[timestamp.Length + 1];
        for (var i = 0; i < timestamp.Length; i++)
        {
            prefix[i] = (byte)timestamp[i];
        }

        prefix[^1] = (byte)'.';

        var body = request.Body.Span;
        Span<byte> expected = stackalloc byte[32];
        Span<byte> candidate = stackalloc byte[32];
        var matched = false;
        foreach (var key in _keys)
        {
            WebhookCrypto.Hmac(HashAlgorithmName.SHA256, key, prefix, body, expected);

            // Every candidate is compared, whichever matches first, so the time taken does not tell which secret or
            // which signature was the right one.
            remaining = header.AsSpan();
            while (WebhookCrypto.TryTakeToken(ref remaining, ",", out item))
            {
                item = item.Trim();
                if (item.StartsWith("v1=", StringComparison.Ordinal)
                    && WebhookCrypto.TryDecodeHex(item[3..], candidate, out var written)
                    && written == candidate.Length)
                {
                    matched |= CryptographicOperations.FixedTimeEquals(expected, candidate);
                }
            }
        }

        if (!matched)
        {
            return WebhookVerification.Invalid(WebhookRejection.NoMatchingSignature);
        }

        // The id of the event is in the signed body, so the default mapping reads it from there.
        return WebhookCrypto.IsWithinTolerance(seconds, now, _tolerance)
            ? WebhookVerification.Valid()
            : WebhookVerification.Invalid(WebhookRejection.OutsideTolerance);
    }
}
