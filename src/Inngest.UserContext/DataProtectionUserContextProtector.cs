// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.

using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.DataProtection;

namespace Inngest.UserContext;

// The default protector. Data Protection signs and encrypts with a key of the shared key ring and writes the key's id
// into the text, so a text made before a key rotation is read as long as the ring still has the key. The purposes bind the
// text to this use: a payload another feature of the same app protected, a cookie for one, is not accepted.
internal sealed class DataProtectionUserContextProtector : IUserContextProtector
{
    internal const string Purpose = "Inngest.UserContext";
    internal const string PurposeVersion = "v1";

    // What Data Protection puts before the part its key encrypts: a four byte header, 0x09F0C9F0 with the version in its
    // last nibble, and the 16 bytes of the key's id.
    private const int HeaderLength = 4 + 16;

    private static readonly UTF8Encoding Strict = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private readonly IDataProtector _protector;

    public DataProtectionUserContextProtector(IDataProtectionProvider provider) =>
        _protector = provider.CreateProtector(Purpose, PurposeVersion);

    public string Protect(string plaintext) => _protector.Protect(plaintext);

    // Data Protection lets the CryptographicException of a payload that is not valid through as it is, and wraps every other
    // failure in one with the original as its InnerException: a key store that cannot be read, for one. The first is the
    // text's fault and the second is not, so only the first is an answer. The text is decoded here, and not by the string
    // extension of Data Protection, which wraps a text that is not base64 the same way.
    public bool TryUnprotect(string protectedText, [NotNullWhen(true)] out string? plaintext)
    {
        plaintext = null;
        if (!TryDecode(protectedText, out var bytes))
        {
            return false;
        }

        try
        {
            plaintext = Strict.GetString(_protector.Unprotect(bytes));
            return true;
        }
        catch (CryptographicException exception) when (exception.InnerException is null || !HasHeader(bytes))
        {
            return false;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    // Base 64 with - and _ and no padding, which is how Data Protection writes the bytes of a string.
    private static bool TryDecode(string text, [NotNullWhen(true)] out byte[]? bytes)
    {
        bytes = null;
        if (text.Length % 4 == 1)
        {
            return false;
        }

        var standard = text.Replace('-', '+').Replace('_', '/') + (text.Length % 4) switch { 2 => "==", 3 => "=", _ => string.Empty };
        var buffer = new byte[(standard.Length / 4 * 3) + 3];
        if (!Convert.TryFromBase64String(standard, buffer, out var written))
        {
            return false;
        }

        bytes = buffer[..written];
        return true;
    }

    private static bool HasHeader(byte[] bytes) =>
        bytes.Length > HeaderLength && bytes[0] == 0x09 && bytes[1] == 0xF0 && bytes[2] == 0xC9 && (bytes[3] & 0xF0) == 0xF0;
}
