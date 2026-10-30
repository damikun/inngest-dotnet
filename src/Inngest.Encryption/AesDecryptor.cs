// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Inngest.Encryption;

// Reads what CryptoJS writes for AES.encrypt(text, passphrase), which is the data of the TypeScript package's
// "inngest/aes" strategy and of the v0 envelopes of its first versions: the standard base64 of "Salted__", an 8-byte
// salt and the ciphertext. The passphrase and salt become an AES-256 key and an IV with OpenSSL's EVP_BytesToKey over
// MD5, one round, and the text is AES-CBC with PKCS7 padding. The format has no authentication, so nothing says a key
// is right except that the text is JSON; this exists to read old data and nothing is ever written in it.
internal sealed class AesDecryptor
{
    public const string StrategyName = "inngest/aes";

    private const int KeySize = 32;
    private const int IvSize = 16;
    private const int SaltSize = 8;

    private static readonly byte[] SaltedPrefix = "Salted__"u8.ToArray();

    private readonly byte[][] _passphrases;

    // The keys are already trimmed and none is empty.
    public AesDecryptor(IReadOnlyList<string> keys)
    {
        _passphrases = [.. keys.Select(static key => Encoding.UTF8.GetBytes(key))];
    }

    public byte[] Decrypt(string data)
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

        var header = SaltedPrefix.Length + SaltSize;
        if (bytes.Length > header && bytes.AsSpan(0, SaltedPrefix.Length).SequenceEqual(SaltedPrefix))
        {
            var salt = bytes.AsSpan(SaltedPrefix.Length, SaltSize);
            var ciphertext = bytes.AsSpan(header);
            foreach (var passphrase in _passphrases)
            {
                if (TryDecrypt(passphrase, salt, ciphertext, out var json))
                {
                    return json;
                }
            }
        }

        throw new CryptographicException("The encrypted value could not be decrypted with any of the configured keys.");
    }

    private static bool TryDecrypt(byte[] passphrase, ReadOnlySpan<byte> salt, ReadOnlySpan<byte> ciphertext, [NotNullWhen(true)] out byte[]? json)
    {
        Span<byte> key = stackalloc byte[KeySize];
        Span<byte> iv = stackalloc byte[IvSize];
        DeriveKeyAndIv(passphrase, salt, key, iv);

        try
        {
            using var aes = Aes.Create();
            aes.Key = key.ToArray();
            json = aes.DecryptCbc(ciphertext, iv, PaddingMode.PKCS7);
            return IsJson(json);
        }
        catch (CryptographicException)
        {
            json = null;
            return false;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    // OpenSSL's EVP_BytesToKey with MD5 and one iteration: each round hashes the previous digest, the passphrase and
    // the salt, and the digests in a row are the key followed by the IV.
    private static void DeriveKeyAndIv(byte[] passphrase, ReadOnlySpan<byte> salt, Span<byte> key, Span<byte> iv)
    {
        const int DigestSize = 16;
        var input = new byte[DigestSize + passphrase.Length + salt.Length];
        passphrase.CopyTo(input, DigestSize);
        salt.CopyTo(input.AsSpan(DigestSize + passphrase.Length));

        Span<byte> derived = stackalloc byte[KeySize + IvSize];
        for (var offset = 0; offset < derived.Length; offset += DigestSize)
        {
            var digest = derived.Slice(offset, DigestSize);
            MD5.HashData(input.AsSpan(offset == 0 ? DigestSize : 0), digest);
            digest.CopyTo(input);
        }

        derived[..KeySize].CopyTo(key);
        derived[KeySize..].CopyTo(iv);
        CryptographicOperations.ZeroMemory(derived);
        CryptographicOperations.ZeroMemory(input);
    }

    private static bool IsJson(ReadOnlySpan<byte> text)
    {
        try
        {
            var reader = new Utf8JsonReader(text);
            while (reader.Read())
            {
            }

            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
