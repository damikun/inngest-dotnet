// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.

using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;

namespace Inngest.UserContext;

// The default protector. Data Protection signs and encrypts with a key of the shared key ring and writes the key's id
// into the text, so a text made before a key rotation is read as long as the ring still has the key. The purposes bind the
// text to this use: a payload another feature of the same app protected, a cookie for one, is not accepted.
internal sealed class DataProtectionUserContextProtector : IUserContextProtector
{
    internal const string Purpose = "Inngest.UserContext";
    internal const string PurposeVersion = "v1";

    private readonly IDataProtector _protector;

    public DataProtectionUserContextProtector(IDataProtectionProvider provider) =>
        _protector = provider.CreateProtector(Purpose, PurposeVersion);

    public string Protect(string plaintext) => _protector.Protect(plaintext);

    // Data Protection reports every failure as a CryptographicException, from a changed payload to a key store that
    // cannot be read, so this cannot tell them apart. Its own logging has the cause.
    public bool TryUnprotect(string protectedText, [NotNullWhen(true)] out string? plaintext)
    {
        try
        {
            plaintext = _protector.Unprotect(protectedText);
            return true;
        }
        catch (CryptographicException)
        {
            plaintext = null;
            return false;
        }
    }
}
