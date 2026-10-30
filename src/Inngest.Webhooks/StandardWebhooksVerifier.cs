// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.

using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using Inngest.Webhooks;

namespace Inngest;

/// <summary>
/// Verifies webhooks that follow the Standard Webhooks specification (standardwebhooks.com), which Svix and so Clerk and
/// Resend send. The signature covers the id, the timestamp and the body, as <c>{id}.{timestamp}.{body}</c>, signed with
/// HMAC-SHA256 and the key inside the secret. The headers are <c>webhook-id</c>, <c>webhook-timestamp</c> and
/// <c>webhook-signature</c>, or the same with a <c>svix-</c> prefix, which Svix sends.
/// </summary>
/// <remarks>
/// The signature header is a space separated list so that a secret can be replaced without downtime: the sender signs
/// with the old and the new one, and a request is accepted when any signature matches any of the secrets given here.
/// Only <c>v1</c> signatures are checked. The asymmetric <c>v1a</c> signatures of the specification are ignored: .NET has
/// no Ed25519 in its libraries, and a request that has only those is rejected. A timestamp more than the tolerance away
/// from the time of arrival, in either direction, rejects the request, after its signature has been checked.
/// </remarks>
public sealed class StandardWebhooksVerifier : IWebhookVerifier
{
    private const string SecretPrefix = "whsec_";

    // The id is at most this long and is ASCII, then a dot, a timestamp of at most 19 digits and a dot.
    private const int PrefixCapacity = 320;

    private static readonly (string Id, string Timestamp, string Signature)[] Families =
    [
        ("webhook-id", "webhook-timestamp", "webhook-signature"),
        ("svix-id", "svix-timestamp", "svix-signature"),
    ];

    private readonly byte[][] _keys;
    private readonly TimeSpan _tolerance;

    /// <summary>Verifies with one secret: <c>whsec_</c> followed by base64, as the provider shows it.</summary>
    /// <param name="secret">The signing secret of the endpoint.</param>
    /// <param name="tolerance">How far the timestamp may be from now. Five minutes when null.</param>
    public StandardWebhooksVerifier(string secret, TimeSpan? tolerance = null)
        : this([secret], tolerance)
    {
    }

    /// <summary>
    /// Verifies with several secrets, such as the new and the old one while a secret is replaced. A signature that
    /// matches any of them is accepted.
    /// </summary>
    /// <param name="secrets">The signing secrets of the endpoint, each <c>whsec_</c> followed by base64.</param>
    /// <param name="tolerance">How far the timestamp may be from now. Five minutes when null.</param>
    public StandardWebhooksVerifier(IEnumerable<string> secrets, TimeSpan? tolerance = null)
    {
        ArgumentNullException.ThrowIfNull(secrets);
        var keys = new List<byte[]>();
        foreach (var secret in secrets)
        {
            keys.Add(DecodeSecret(secret));
        }

        _keys = keys.Count > 0 ? [.. keys] : throw new ArgumentException("At least one webhook secret is needed.", nameof(secrets));
        _tolerance = WebhookCrypto.Tolerance(tolerance, nameof(tolerance));
    }

    /// <inheritdoc />
    public WebhookVerification Verify(IWebhookRequest request, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!TryReadHeaders(request, out var id, out var timestamp, out var signatures))
        {
            return WebhookVerification.Invalid(WebhookRejection.MissingHeaders);
        }

        // The id sits between two dots in the signed text, so one with a dot in it could be split somewhere else. The
        // standard tells senders never to send such an id; refusing it keeps two ids from signing the same text.
        if (!WebhookCrypto.IsToken(id) || id.Contains('.') || !WebhookCrypto.TryParseTimestamp(timestamp, out var seconds))
        {
            return WebhookVerification.Invalid(WebhookRejection.MalformedHeaders);
        }

        // The text is signed as it was sent, so the timestamp is not formatted again.
        Span<byte> prefix = stackalloc byte[PrefixCapacity];
        var length = Encoding.ASCII.GetBytes(id, prefix);
        prefix[length++] = (byte)'.';
        length += Encoding.ASCII.GetBytes(timestamp, prefix[length..]);
        prefix[length++] = (byte)'.';

        var body = request.Body.Span;
        Span<byte> expected = stackalloc byte[32];
        Span<byte> candidate = stackalloc byte[32];
        var matched = false;
        foreach (var key in _keys)
        {
            WebhookCrypto.Hmac(HashAlgorithmName.SHA256, key, prefix[..length], body, expected);

            // Every candidate is compared, whichever matches first, so the time taken does not tell which secret or
            // which signature was the right one.
            var remaining = signatures.AsSpan();
            while (WebhookCrypto.TryTakeToken(ref remaining, " \t", out var token))
            {
                if (token.StartsWith("v1,", StringComparison.Ordinal)
                    && WebhookCrypto.TryDecodeBase64(token[3..], candidate, out var written)
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

        return WebhookCrypto.IsWithinTolerance(seconds, now, _tolerance)
            ? WebhookVerification.Valid(id)
            : WebhookVerification.Invalid(WebhookRejection.OutsideTolerance);
    }

    // The key is the base64 after whsec_, decoded. Stripe's secrets look the same but are used as text, not decoded.
    private static byte[] DecodeSecret(string? secret)
    {
        if (string.IsNullOrWhiteSpace(secret))
        {
            throw new ArgumentException("A webhook secret is empty.", nameof(secret));
        }

        var text = secret.AsSpan().Trim();
        if (text.StartsWith(SecretPrefix, StringComparison.Ordinal))
        {
            text = text[SecretPrefix.Length..];
        }

        var key = new byte[(text.Length / 4 * 3) + 3];
        if (!Convert.TryFromBase64Chars(text, key, out var written) || written == 0)
        {
            throw new ArgumentException("A webhook secret must be whsec_ followed by base64.", nameof(secret));
        }

        return key[..written];
    }

    // A request carries one family of headers, complete. The standard's own comes first.
    private static bool TryReadHeaders(
        IWebhookRequest request,
        [NotNullWhen(true)] out string? id,
        [NotNullWhen(true)] out string? timestamp,
        [NotNullWhen(true)] out string? signature)
    {
        foreach (var family in Families)
        {
            id = WebhookCrypto.Header(request, family.Id);
            timestamp = WebhookCrypto.Header(request, family.Timestamp);
            signature = WebhookCrypto.Header(request, family.Signature);
            if (id is not null && timestamp is not null && signature is not null)
            {
                return true;
            }
        }

        id = timestamp = signature = null;
        return false;
    }
}
