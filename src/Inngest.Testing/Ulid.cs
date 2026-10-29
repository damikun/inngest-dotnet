// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using System.Security.Cryptography;

namespace Inngest.Testing;

// Ids in the format Inngest gives runs and events: ULIDs, 48 bits of Unix milliseconds and 80 random bits in
// Crockford's base 32.
internal static class Ulid
{
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    public static string New(DateTimeOffset time)
    {
        Span<byte> random = stackalloc byte[10];
        RandomNumberGenerator.Fill(random);

        Span<char> chars = stackalloc char[26];
        Encode((ulong)Math.Max(0, time.ToUnixTimeMilliseconds()), chars[..10]);
        Encode(ReadUInt40(random[..5]), chars[10..18]);
        Encode(ReadUInt40(random[5..]), chars[18..]);
        return new string(chars);
    }

    // Writes the low five bits per character of value, most significant first.
    private static void Encode(ulong value, Span<char> chars)
    {
        for (var i = chars.Length - 1; i >= 0; i--)
        {
            chars[i] = Alphabet[(int)(value & 31)];
            value >>= 5;
        }
    }

    private static ulong ReadUInt40(ReadOnlySpan<byte> bytes)
    {
        ulong value = 0;
        foreach (var item in bytes)
        {
            value = (value << 8) | item;
        }

        return value;
    }
}
