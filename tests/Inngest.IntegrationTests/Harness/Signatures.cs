using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Inngest.IntegrationTests.Harness;

// Inngest's X-Inngest-Signature, computed here independently of the SDK so that tests can sign requests to the app and
// check the app's signed responses: "t=<unix seconds>&s=<hex HMAC-SHA256 of the raw body followed by t>", keyed with
// the signing key's text. Test keys are plain hex, so there is no signkey-<env>- prefix to strip.
public static class Signatures
{
    public static string Create(ReadOnlySpan<byte> body, string signingKey, DateTimeOffset signedAt)
    {
        var timestamp = signedAt.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        return $"t={timestamp}&s={Convert.ToHexStringLower(Compute(body, timestamp, signingKey))}";
    }

    public static bool IsValid(string? header, ReadOnlySpan<byte> body, string signingKey)
    {
        if (header is null)
        {
            return false;
        }

        var parts = header.Split('&')
            .Select(part => part.Split('=', 2))
            .Where(pair => pair.Length == 2)
            .ToDictionary(pair => pair[0], pair => pair[1], StringComparer.Ordinal);

        return parts.TryGetValue("t", out var timestamp)
            && parts.TryGetValue("s", out var signature)
            && signature.Length == 64
            && CryptographicOperations.FixedTimeEquals(Compute(body, timestamp, signingKey), Convert.FromHexString(signature));
    }

    private static byte[] Compute(ReadOnlySpan<byte> body, string timestamp, string signingKey)
    {
        using var hmac = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA256, Encoding.UTF8.GetBytes(signingKey));
        hmac.AppendData(body);
        hmac.AppendData(Encoding.ASCII.GetBytes(timestamp));
        return hmac.GetHashAndReset();
    }
}
