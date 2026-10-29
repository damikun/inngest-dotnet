// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Inngest.Signing;

// A signing key as configured, for example "signkey-prod-0a1b...". Inngest signs requests with the part after the
// "signkey-<env>-" prefix, used as UTF-8 text rather than decoded from hex.
internal sealed partial class SigningKey
{
    private SigningKey(string value)
    {
        Value = value;
        var prefixLength = PrefixPattern().Match(value) is { Success: true } match ? match.Length : 0;
        var secret = value.AsSpan(prefixLength);
        HmacKey = Encoding.UTF8.GetBytes(secret.ToString());
        BearerToken = string.Concat(value.AsSpan(0, prefixLength), HashSecret(secret));
    }

    public string Value { get; }

    public byte[] HmacKey { get; }

    // Sent as "Authorization: Bearer <token>" on API calls. Inngest compares it with the SHA-256 of the
    // hex-decoded secret, so the raw key never leaves the process.
    public string BearerToken { get; }

    public static SigningKey? Create(string? value) => string.IsNullOrWhiteSpace(value) ? null : new SigningKey(value.Trim());

    public static string HashEventKey(string eventKey) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(eventKey)));

    private static string HashSecret(ReadOnlySpan<char> secret)
    {
        Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
        var bytes = new byte[(secret.Length + 1) / 2];
        if (Convert.FromHexString(secret, bytes, out _, out var written) == System.Buffers.OperationStatus.Done)
        {
            SHA256.HashData(bytes.AsSpan(0, written), hash);
        }
        else
        {
            // Not a hex key (only seen with hand-written test keys); hash the text so a token still exists.
            SHA256.HashData(Encoding.UTF8.GetBytes(secret.ToString()), hash);
        }

        return Convert.ToHexStringLower(hash);
    }

    [GeneratedRegex(@"^signkey-\w+-")]
    private static partial Regex PrefixPattern();
}
