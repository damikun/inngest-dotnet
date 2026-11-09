// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using System.Diagnostics.CodeAnalysis;
using Inngest.Configuration;
using Inngest.Signing;

namespace Inngest;

/// <summary>
/// Checks the X-Inngest-Signature header of a request from Inngest, as the Inngest endpoint does: an HMAC-SHA256 of the
/// raw body with the signing key, made less than five minutes ago, or with the fallback key while the key is rotated.
/// AddInngest registers one with the app's keys; in dev mode that one accepts every request, since the dev server
/// signs nothing.
/// </summary>
public sealed class InngestSignatureVerifier
{
    private readonly RequestSigner _signer;
    private readonly SigningKey? _signingKey;
    private readonly SigningKey? _fallbackKey;
    private readonly bool _acceptsAll;

    /// <summary>
    /// Checks signatures made with these keys, whatever the app is configured with, on the clock of timeProvider or the
    /// system's.
    /// </summary>
    public InngestSignatureVerifier(string signingKey, string? signingKeyFallback = null, TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(signingKey);
        _signer = new RequestSigner(timeProvider ?? TimeProvider.System);
        _signingKey = SigningKey.Create(signingKey);
        _fallbackKey = SigningKey.Create(signingKeyFallback);
    }

    internal InngestSignatureVerifier(InngestSettings settings, RequestSigner signer)
    {
        _signer = signer;
        _signingKey = settings.SigningKey;
        _fallbackKey = settings.SigningKeyFallback;
        _acceptsAll = settings.IsDev;
    }

    /// <summary>Whether signature, the value of the X-Inngest-Signature header, signs body.</summary>
    public bool Verify(string? signature, ReadOnlySpan<byte> body) => Verify(signature, body, out _);

    /// <summary>Whether signature signs body; when it does not, error says why, in words fit for a log.</summary>
    public bool Verify(string? signature, ReadOnlySpan<byte> body, [NotNullWhen(false)] out string? error)
    {
        if (_acceptsAll)
        {
            error = null;
            return true;
        }

        if (_signingKey is null)
        {
            error = "No signing key is configured.";
            return false;
        }

        var check = _signer.Verify(signature, body, _signingKey, _fallbackKey);
        error = check.IsValid ? null : check.Error ?? "The request signature is invalid.";
        return check.IsValid;
    }
}
