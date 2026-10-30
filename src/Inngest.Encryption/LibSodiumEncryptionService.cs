// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using System.Security.Cryptography;

namespace Inngest.Encryption;

// The default strategy, "inngest/libsodium", as the TypeScript package's LibSodiumEncryptionService does it: each
// key is hashed to 32 bytes, the first one encrypts, and the data is the standard base64 of the 24-byte nonce
// followed by the box that crypto_secretbox_easy makes of the JSON.
internal sealed class LibSodiumEncryptionService : EncryptionService
{
    public const string StrategyName = "inngest/libsodium";

    private readonly byte[][] _keys;

    // The keys are already trimmed and none is empty; the first one encrypts.
    public LibSodiumEncryptionService(IReadOnlyList<string> keys)
    {
        _keys = [.. keys.Select(SecretBox.DeriveKey)];
    }

    public override string Identifier => StrategyName;

    public override string Encrypt(ReadOnlySpan<byte> json)
    {
        var data = new byte[SecretBox.NonceSize + SecretBox.TagSize + json.Length];
        var nonce = data.AsSpan(0, SecretBox.NonceSize);
        RandomNumberGenerator.Fill(nonce);
        SecretBox.Seal(_keys[0], nonce, json, data.AsSpan(SecretBox.NonceSize));
        return Convert.ToBase64String(data);
    }

    public override byte[] Decrypt(string data)
    {
        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(data);
        }
        catch (FormatException exception)
        {
            throw new CryptographicException("The encrypted value is not valid base64.", exception);
        }

        if (bytes.Length < SecretBox.NonceSize + SecretBox.TagSize)
        {
            throw new CryptographicException("The encrypted value is too short to hold a nonce and a tag.");
        }

        var nonce = bytes.AsSpan(0, SecretBox.NonceSize);
        var box = bytes.AsSpan(SecretBox.NonceSize);
        var message = new byte[box.Length - SecretBox.TagSize];
        foreach (var key in _keys)
        {
            if (SecretBox.TryOpen(key, nonce, box, message))
            {
                return message;
            }
        }

        throw new CryptographicException("The encrypted value could not be decrypted with any of the configured keys.");
    }
}
