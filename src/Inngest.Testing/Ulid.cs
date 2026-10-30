// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using System.Security.Cryptography;

namespace Inngest.Testing;

// Ids in the format Inngest gives runs and events: ULIDs, 48 bits of Unix milliseconds and 80 random bits in
// Crockford's base 32. Ids made in the same millisecond count up from the first one's random bits, as in the ULID
// spec's monotonic mode, so that they sort in the order they were made.
internal static class Ulid
{
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
    private const ulong MaxUInt40 = (1UL << 40) - 1;

    private static readonly object Gate = new();
    private static long _lastTime = -1;
    private static ulong _lastHigh;
    private static ulong _lastLow;

    public static string New(DateTimeOffset time)
    {
        var milliseconds = Math.Max(0, time.ToUnixTimeMilliseconds());
        ulong high;
        ulong low;
        lock (Gate)
        {
            if (milliseconds != _lastTime || !TryIncrementLast())
            {
                Span<byte> random = stackalloc byte[10];
                RandomNumberGenerator.Fill(random);
                _lastHigh = ReadUInt40(random[..5]);
                _lastLow = ReadUInt40(random[5..]);
                _lastTime = milliseconds;
            }

            high = _lastHigh;
            low = _lastLow;
        }

        Span<char> chars = stackalloc char[26];
        Encode((ulong)milliseconds, chars[..10]);
        Encode(high, chars[10..18]);
        Encode(low, chars[18..]);
        return new string(chars);
    }

    // Adds one to the 80 random bits; false when they are all ones, and fresh random bits are taken instead.
    private static bool TryIncrementLast()
    {
        if (_lastLow < MaxUInt40)
        {
            _lastLow++;
            return true;
        }

        if (_lastHigh < MaxUInt40)
        {
            _lastHigh++;
            _lastLow = 0;
            return true;
        }

        return false;
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
