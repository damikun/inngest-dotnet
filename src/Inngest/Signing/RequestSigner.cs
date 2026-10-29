// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;

namespace Inngest.Signing;

// Verifies the X-Inngest-Signature header on requests from Inngest and signs responses to them. The header has the
// form "t=<unix seconds>&s=<hex HMAC-SHA256 of body + t>".
internal sealed class RequestSigner(TimeProvider timeProvider)
{
    public static readonly TimeSpan MaxClockSkew = TimeSpan.FromMinutes(5);

    private const int SignatureSize = HMACSHA256.HashSizeInBytes;

    public SignatureCheck Verify(string? header, ReadOnlySpan<byte> body, SigningKey signingKey, SigningKey? fallbackKey, bool allowExpired = false)
    {
        if (string.IsNullOrEmpty(header))
        {
            return SignatureCheck.Failed("The request has no X-Inngest-Signature header.");
        }

        Span<byte> signature = stackalloc byte[SignatureSize];
        if (!TryParseHeader(header, out var timestamp, signature))
        {
            return SignatureCheck.Failed("The X-Inngest-Signature header is malformed.");
        }

        // Compared in seconds: any timestamp the header can carry must be rejected, not throw.
        if (!allowExpired && Math.Abs(timeProvider.GetUtcNow().ToUnixTimeSeconds() - timestamp) > (long)MaxClockSkew.TotalSeconds)
        {
            return SignatureCheck.Failed("The request signature has expired.");
        }

        if (Matches(body, timestamp, signature, signingKey))
        {
            return SignatureCheck.Verified(signingKey);
        }

        if (fallbackKey is not null && Matches(body, timestamp, signature, fallbackKey))
        {
            return SignatureCheck.Verified(fallbackKey);
        }

        return SignatureCheck.Failed("The request signature is invalid.");
    }

    public string Sign(ReadOnlySpan<byte> body, SigningKey key)
    {
        var timestamp = timeProvider.GetUtcNow().ToUnixTimeSeconds();
        Span<byte> mac = stackalloc byte[SignatureSize];
        ComputeMac(body, timestamp, key, mac);
        return string.Create(CultureInfo.InvariantCulture, $"t={timestamp}&s={Convert.ToHexStringLower(mac)}");
    }

    private static bool Matches(ReadOnlySpan<byte> body, long timestamp, ReadOnlySpan<byte> signature, SigningKey key)
    {
        Span<byte> expected = stackalloc byte[SignatureSize];
        ComputeMac(body, timestamp, key, expected);
        return CryptographicOperations.FixedTimeEquals(expected, signature);
    }

    private static void ComputeMac(ReadOnlySpan<byte> body, long timestamp, SigningKey key, Span<byte> destination)
    {
        Span<byte> timestampText = stackalloc byte[20];
        timestamp.TryFormat(timestampText, out var timestampLength, provider: CultureInfo.InvariantCulture);

        using var hmac = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA256, key.HmacKey);
        hmac.AppendData(body);
        hmac.AppendData(timestampText[..timestampLength]);
        hmac.GetHashAndReset(destination);
    }

    private static bool TryParseHeader(string header, out long timestamp, Span<byte> signature)
    {
        timestamp = 0;
        ReadOnlySpan<char> t = default;
        ReadOnlySpan<char> s = default;
        var remaining = header.AsSpan();
        foreach (var range in remaining.Split('&'))
        {
            var pair = remaining[range];
            var separator = pair.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }

            var name = pair[..separator];
            if (name.SequenceEqual("t"))
            {
                t = pair[(separator + 1)..];
            }
            else if (name.SequenceEqual("s"))
            {
                s = pair[(separator + 1)..];
            }
        }

        return long.TryParse(t, NumberStyles.None, CultureInfo.InvariantCulture, out timestamp)
            && s.Length == SignatureSize * 2
            && Convert.FromHexString(s, signature, out _, out var written) == OperationStatus.Done
            && written == SignatureSize;
    }
}

internal readonly record struct SignatureCheck(bool IsValid, SigningKey? KeyUsed, string? Error)
{
    public static SignatureCheck Verified(SigningKey key) => new(true, key, null);

    public static SignatureCheck Failed(string error) => new(false, null, error);
}
