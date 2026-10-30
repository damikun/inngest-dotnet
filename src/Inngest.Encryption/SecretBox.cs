// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using System.Security.Cryptography;
using System.Text;
using Org.BouncyCastle.Crypto.Digests;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.Macs;
using Org.BouncyCastle.Crypto.Parameters;

namespace Inngest.Encryption;

// libsodium's crypto_generichash and crypto_secretbox_easy, which the TypeScript package calls, on the primitives of
// BouncyCastle. A box is the 16-byte Poly1305 tag followed by the message encrypted with XSalsa20. As in NaCl, the
// first 32 bytes of the key stream are the Poly1305 key and the message is encrypted with the stream from byte 32 on.
internal static class SecretBox
{
    public const int KeySize = 32;
    public const int NonceSize = 24;
    public const int TagSize = 16;

    // crypto_generichash(32, passphrase): BLAKE2b with a 32-byte digest and no key, over the UTF-8 bytes of the passphrase.
    public static byte[] DeriveKey(string passphrase)
    {
        var digest = new Blake2bDigest(KeySize * 8);
        digest.BlockUpdate(Encoding.UTF8.GetBytes(passphrase));
        var key = new byte[KeySize];
        digest.DoFinal(key);
        return key;
    }

    // Writes the tag and the encrypted message to box, which is TagSize bytes longer than the message.
    public static void Seal(ReadOnlySpan<byte> key, ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> message, Span<byte> box)
    {
        Span<byte> tagKey = stackalloc byte[KeySize];
        var stream = StartStream(key, nonce, tagKey);
        var encrypted = box[TagSize..];
        stream.ProcessBytes(message, encrypted);
        Authenticate(tagKey, encrypted, box[..TagSize]);
        CryptographicOperations.ZeroMemory(tagKey);
    }

    // Decrypts into message, which is TagSize bytes shorter than the box. Nothing is decrypted unless the tag matches.
    public static bool TryOpen(ReadOnlySpan<byte> key, ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> box, Span<byte> message)
    {
        Span<byte> tagKey = stackalloc byte[KeySize];
        var stream = StartStream(key, nonce, tagKey);
        var encrypted = box[TagSize..];
        Span<byte> tag = stackalloc byte[TagSize];
        Authenticate(tagKey, encrypted, tag);
        CryptographicOperations.ZeroMemory(tagKey);
        if (!CryptographicOperations.FixedTimeEquals(tag, box[..TagSize]))
        {
            return false;
        }

        stream.ProcessBytes(encrypted, message);
        return true;
    }

    // The key stream positioned after the Poly1305 key, which is written to tagKey.
    private static XSalsa20Engine StartStream(ReadOnlySpan<byte> key, ReadOnlySpan<byte> nonce, Span<byte> tagKey)
    {
        var stream = new XSalsa20Engine();
        stream.Init(true, new ParametersWithIV(new KeyParameter(key), nonce.ToArray()));
        Span<byte> zeros = stackalloc byte[KeySize];
        stream.ProcessBytes(zeros, tagKey);
        return stream;
    }

    private static void Authenticate(ReadOnlySpan<byte> tagKey, ReadOnlySpan<byte> encrypted, Span<byte> tag)
    {
        var mac = new Poly1305();
        mac.Init(new KeyParameter(tagKey));
        mac.BlockUpdate(encrypted);
        mac.DoFinal(tag);
    }
}
