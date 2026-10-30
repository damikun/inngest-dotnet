// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.

using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Inngest.Webhooks;

// What every verifier shares. Everything here takes input from callers nobody has authenticated yet, so nothing throws
// for what a request contains: decoding reports failure and the verifiers turn it into a rejection.
internal static class WebhookCrypto
{
    // The digest of the longest HMAC supported (SHA-512).
    public const int MaxDigestSize = 64;

    // The longest id or event name taken from a request.
    private const int MaxTokenLength = 256;

    // Over this long a delivery is a replay or a clock that has drifted. It is the window Stripe's and Svix's libraries
    // use, so it covers the delay of a delivery without leaving a replay open for long.
    public static readonly TimeSpan DefaultTolerance = TimeSpan.FromMinutes(5);

    // Secrets used as they are written: Stripe, GitHub, Shopify and most providers sign with the text of the secret.
    // A secret never goes into a message, so a mistake in configuration does not put it in a log.
    public static byte[][] TextKeys(IEnumerable<string> secrets, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(secrets);
        var strict = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
        var keys = new List<byte[]>();
        foreach (var secret in secrets)
        {
            if (string.IsNullOrWhiteSpace(secret))
            {
                throw new ArgumentException("A webhook secret is empty.", parameterName);
            }

            try
            {
                keys.Add(strict.GetBytes(secret));
            }
            catch (EncoderFallbackException)
            {
                throw new ArgumentException("A webhook secret is not valid text.", parameterName);
            }
        }

        return keys.Count > 0 ? [.. keys] : throw new ArgumentException("At least one webhook secret is needed.", parameterName);
    }

    public static TimeSpan Tolerance(TimeSpan? tolerance, string parameterName)
    {
        var value = tolerance ?? DefaultTolerance;
        return value > TimeSpan.Zero
            ? value
            : throw new ArgumentOutOfRangeException(parameterName, value, "The tolerance must be longer than zero.");
    }

    // The value of a header without the white space around it, or null when it is absent or blank.
    public static string? Header(IWebhookRequest request, string name)
    {
        var value = request.GetHeader(name)?.Trim();
        return string.IsNullOrEmpty(value) ? null : value;
    }

    // Ids and event names come from a request, so before they reach an event they must be short, visible ASCII: no
    // spaces, controls or look-alike characters.
    public static bool IsToken([NotNullWhen(true)] string? value)
    {
        if (value is null || value.Length is 0 or > MaxTokenLength)
        {
            return false;
        }

        foreach (var c in value)
        {
            if (c is < '!' or > '~')
            {
                return false;
            }
        }

        return true;
    }

    // Seconds since the epoch as digits only: no sign, no space and no leading plus, which the signed text could not
    // have been written with.
    public static bool TryParseTimestamp(ReadOnlySpan<char> text, out long seconds)
    {
        seconds = 0;
        return text.Length is > 0 and <= 19
            && long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out seconds);
    }

    public static bool IsWithinTolerance(long timestamp, DateTimeOffset now, TimeSpan tolerance) =>
        Math.Abs(now.ToUnixTimeSeconds() - timestamp) <= tolerance.TotalSeconds;

    public static int Hmac(HashAlgorithmName algorithm, byte[] key, ReadOnlySpan<byte> prefix, ReadOnlySpan<byte> body, Span<byte> destination)
    {
        // The body is added to the hash as it is, instead of being copied behind the prefix.
        using var hmac = IncrementalHash.CreateHMAC(algorithm, key);
        if (!prefix.IsEmpty)
        {
            hmac.AppendData(prefix);
        }

        hmac.AppendData(body);
        return hmac.GetHashAndReset(destination);
    }

    public static bool TryDecodeHex(ReadOnlySpan<char> text, Span<byte> destination, out int written)
    {
        written = 0;
        if (text.Length == 0 || (text.Length & 1) != 0 || text.Length / 2 > destination.Length)
        {
            return false;
        }

        for (var i = 0; i < text.Length; i += 2)
        {
            var high = HexValue(text[i]);
            var low = HexValue(text[i + 1]);
            if ((high | low) < 0)
            {
                return false;
            }

            destination[i / 2] = (byte)((high << 4) | low);
        }

        written = text.Length / 2;
        return true;
    }

    // Reports bad characters, bad padding and a result that does not fit as false, where Convert.FromBase64String throws.
    public static bool TryDecodeBase64(ReadOnlySpan<char> text, Span<byte> destination, out int written) =>
        Convert.TryFromBase64Chars(text, destination, out written);

    // Cuts the next piece off text, where pieces are separated by any of the separator characters. Empty pieces are
    // skipped, so a doubled or trailing separator is harmless.
    public static bool TryTakeToken(scoped ref ReadOnlySpan<char> text, ReadOnlySpan<char> separators, out ReadOnlySpan<char> token)
    {
        while (!text.IsEmpty)
        {
            var end = text.IndexOfAny(separators);
            if (end < 0)
            {
                token = text;
                text = default;
                return true;
            }

            token = text[..end];
            text = text[(end + 1)..];
            if (!token.IsEmpty)
            {
                return true;
            }
        }

        token = default;
        return false;
    }

    private static int HexValue(char c) => c switch
    {
        >= '0' and <= '9' => c - '0',
        >= 'a' and <= 'f' => c - 'a' + 10,
        >= 'A' and <= 'F' => c - 'A' + 10,
        _ => -1,
    };
}

// Why a request was rejected, as the log says it. Fixed texts, so that nothing from the request reaches a log.
internal static class WebhookRejection
{
    public const string MissingHeaders = "a header that carries the signature, the timestamp or the id is missing";
    public const string MalformedHeaders = "a header that carries the signature, the timestamp or the id is malformed";
    public const string NoMatchingSignature = "no signature matches the body for any secret";
    public const string OutsideTolerance = "the timestamp is outside the tolerance";
}
