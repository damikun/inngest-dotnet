// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.

using System.Security.Cryptography;
using System.Text;

namespace Inngest.Webhooks;

// The verifier behind GitHub, Shopify and the generic one: an HMAC of the body, optionally behind a timestamp, written
// in a header.
internal sealed class HmacHeaderVerifier
{
    // What the options may add in front of the body, so that the text fits a buffer on the stack.
    private const int MaxPrefixBytes = 128;
    private const int MaxTimestampDigits = 19;

    private readonly byte[][] _keys;
    private readonly HashAlgorithmName _algorithm;
    private readonly int _digestSize;
    private readonly HmacSignatureEncoding _encoding;
    private readonly string _signatureHeader;
    private readonly string? _signaturePrefix;
    private readonly string? _timestampHeader;
    private readonly byte[] _contentPrefix;
    private readonly byte[] _separator;
    private readonly TimeSpan _tolerance;
    private readonly string? _deliveryIdHeader;

    public HmacHeaderVerifier(HmacWebhookOptions options, byte[][] keys)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.SignatureHeader, nameof(options));
        _keys = keys;
        _algorithm = options.Algorithm;
        _digestSize = DigestSize(options.Algorithm);
        _encoding = options.SignatureEncoding is HmacSignatureEncoding.Hex or HmacSignatureEncoding.Base64
            ? options.SignatureEncoding
            : throw new ArgumentOutOfRangeException(nameof(options), "The signature encoding is not known.");
        _signatureHeader = options.SignatureHeader;
        _signaturePrefix = string.IsNullOrEmpty(options.SignaturePrefix) ? null : options.SignaturePrefix;
        _timestampHeader = string.IsNullOrWhiteSpace(options.TimestampHeader) ? null : options.TimestampHeader;
        _contentPrefix = Encoding.UTF8.GetBytes(options.SignedContentPrefix ?? "");
        _separator = Encoding.UTF8.GetBytes(options.TimestampSeparator ?? "");
        _tolerance = WebhookCrypto.Tolerance(options.Tolerance, nameof(options));
        _deliveryIdHeader = string.IsNullOrWhiteSpace(options.DeliveryIdHeader) ? null : options.DeliveryIdHeader;

        if (_contentPrefix.Length + _separator.Length > MaxPrefixBytes)
        {
            throw new ArgumentException($"The signed content prefix and the timestamp separator may be {MaxPrefixBytes} bytes together.", nameof(options));
        }
    }

    public WebhookVerification Verify(IWebhookRequest request, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(request);
        var header = WebhookCrypto.Header(request, _signatureHeader);
        if (header is null)
        {
            return WebhookVerification.Invalid(WebhookRejection.MissingHeaders);
        }

        var timestamp = default(string);
        long seconds = 0;
        if (_timestampHeader is not null)
        {
            timestamp = WebhookCrypto.Header(request, _timestampHeader);
            if (timestamp is null)
            {
                return WebhookVerification.Invalid(WebhookRejection.MissingHeaders);
            }

            if (!WebhookCrypto.TryParseTimestamp(timestamp, out seconds))
            {
                return WebhookVerification.Invalid(WebhookRejection.MalformedHeaders);
            }
        }

        // The text that is signed before the body, as the provider wrote it.
        Span<byte> prefix = stackalloc byte[MaxPrefixBytes + MaxTimestampDigits];
        _contentPrefix.CopyTo(prefix);
        var length = _contentPrefix.Length;
        if (timestamp is not null)
        {
            length += Encoding.ASCII.GetBytes(timestamp, prefix[length..]);
            _separator.CopyTo(prefix[length..]);
            length += _separator.Length;
        }

        var body = request.Body.Span;
        Span<byte> expected = stackalloc byte[WebhookCrypto.MaxDigestSize];
        Span<byte> candidate = stackalloc byte[WebhookCrypto.MaxDigestSize];
        expected = expected[.._digestSize];
        candidate = candidate[.._digestSize];
        var matched = false;
        foreach (var key in _keys)
        {
            WebhookCrypto.Hmac(_algorithm, key, prefix[..length], body, expected);

            // Every candidate is compared, whichever matches first, so the time taken does not tell which secret or
            // which signature was the right one.
            var remaining = header.AsSpan();
            while (WebhookCrypto.TryTakeToken(ref remaining, ", \t", out var token))
            {
                if (_signaturePrefix is not null)
                {
                    if (!token.StartsWith(_signaturePrefix, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    token = token[_signaturePrefix.Length..];
                }

                var decoded = _encoding == HmacSignatureEncoding.Hex
                    ? WebhookCrypto.TryDecodeHex(token, candidate, out var written)
                    : WebhookCrypto.TryDecodeBase64(token, candidate, out written);
                if (decoded && written == candidate.Length)
                {
                    matched |= CryptographicOperations.FixedTimeEquals(expected, candidate);
                }
            }
        }

        if (!matched)
        {
            return WebhookVerification.Invalid(WebhookRejection.NoMatchingSignature);
        }

        if (timestamp is not null && !WebhookCrypto.IsWithinTolerance(seconds, now, _tolerance))
        {
            return WebhookVerification.Invalid(WebhookRejection.OutsideTolerance);
        }

        // The id travels in a header that the signature does not cover for most providers, so it is checked for its shape.
        var deliveryId = _deliveryIdHeader is null ? null : WebhookCrypto.Header(request, _deliveryIdHeader);
        return WebhookVerification.Valid(WebhookCrypto.IsToken(deliveryId) ? deliveryId : null);
    }

    private static int DigestSize(HashAlgorithmName algorithm)
    {
        if (algorithm == HashAlgorithmName.SHA1)
        {
            return 20;
        }

        if (algorithm == HashAlgorithmName.SHA256)
        {
            return 32;
        }

        if (algorithm == HashAlgorithmName.SHA384)
        {
            return 48;
        }

        return algorithm == HashAlgorithmName.SHA512
            ? 64
            : throw new ArgumentException("The algorithm must be SHA-1, SHA-256, SHA-384 or SHA-512.", nameof(algorithm));
    }
}
