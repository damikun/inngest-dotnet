// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.

using System.Security.Cryptography;

namespace Inngest;

/// <summary>
/// How a provider signs its webhooks with an HMAC, for <see cref="HmacWebhookVerifier"/>. The signed text is
/// <see cref="SignedContentPrefix"/>, then the value of <see cref="TimestampHeader"/> and
/// <see cref="TimestampSeparator"/> when there is a timestamp header, then the raw body. GitHub, which signs the body
/// only, and Slack, which signs <c>v0:{timestamp}:{body}</c>, are both a few lines of options.
/// </summary>
public sealed record HmacWebhookOptions
{
    /// <summary>The header that carries the signature, such as <c>X-Hub-Signature-256</c>.</summary>
    public required string SignatureHeader { get; init; }

    /// <summary>The hash of the HMAC: SHA-256 unless the provider says otherwise. SHA-1, SHA-384 and SHA-512 are supported.</summary>
    public HashAlgorithmName Algorithm { get; init; } = HashAlgorithmName.SHA256;

    /// <summary>How the signature is written in the header: hexadecimal unless the provider says otherwise.</summary>
    public HmacSignatureEncoding SignatureEncoding { get; init; } = HmacSignatureEncoding.Hex;

    /// <summary>
    /// Text before the signature in the header, such as <c>sha256=</c> or <c>v0=</c>. A header can hold several
    /// signatures, separated by commas or white space, each with the prefix.
    /// </summary>
    public string? SignaturePrefix { get; init; }

    /// <summary>
    /// The header that carries the time the provider sent the request, in seconds since 1970, when the provider signs
    /// it. A request whose timestamp is more than <see cref="Tolerance"/> from the time of arrival is rejected, which
    /// is what stops a captured request from being sent again later.
    /// </summary>
    public string? TimestampHeader { get; init; }

    /// <summary>Text at the start of the signed text, such as <c>v0:</c> for Slack. Empty for none.</summary>
    public string SignedContentPrefix { get; init; } = "";

    /// <summary>What goes between the timestamp and the body in the signed text: a dot unless the provider says otherwise. Empty for none.</summary>
    public string TimestampSeparator { get; init; } = ".";

    /// <summary>How far the timestamp may be from now, when there is a <see cref="TimestampHeader"/>. Five minutes by default.</summary>
    public TimeSpan Tolerance { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// The header that carries the id of the delivery, such as <c>X-GitHub-Delivery</c>, which becomes the id of the
    /// event so that a delivery sent again is not run twice. Null for a provider without one.
    /// </summary>
    public string? DeliveryIdHeader { get; init; }
}
