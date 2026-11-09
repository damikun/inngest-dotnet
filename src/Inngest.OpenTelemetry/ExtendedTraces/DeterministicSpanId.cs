// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace Inngest.OpenTelemetry.ExtendedTraces;

// The span id the Inngest executor gives a step's span, which it derives from a seed: Go's
// DeterministicSpanConfig(seed).SpanID. The seed's SHA-256 is the key of Go's ChaCha8 generator, whose first 16 bytes
// would be a trace id and whose next 8 are the span id. Those 8 bytes are word 1 of the ChaCha8 blocks with counters 0
// and 1, a word that gets no key added at the end, so only that word of each block is computed.
internal static class DeterministicSpanId
{
    // The id as the 16 lower-case hex digits OpenTelemetry writes.
    public static string Create(string seed)
    {
        Span<byte> key = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(Encoding.UTF8.GetBytes(seed), key);

        Span<uint> words = stackalloc uint[8];
        for (var i = 0; i < words.Length; i++)
        {
            words[i] = BinaryPrimitives.ReadUInt32LittleEndian(key[(i * 4)..]);
        }

        Span<byte> id = stackalloc byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(id, Word1(words, counter: 0));
        BinaryPrimitives.WriteUInt32LittleEndian(id[4..], Word1(words, counter: 1));
        return Convert.ToHexString(id).ToLowerInvariant();
    }

    private static uint Word1(ReadOnlySpan<uint> key, uint counter)
    {
        Span<uint> s = stackalloc uint[16];
        s[0] = 0x61707865;
        s[1] = 0x3320646e;
        s[2] = 0x79622d32;
        s[3] = 0x6b206574;
        key.CopyTo(s[4..12]);
        s[12] = counter;

        // ChaCha8: four double rounds, each a column round and a diagonal round.
        for (var round = 0; round < 4; round++)
        {
            QuarterRound(s, 0, 4, 8, 12);
            QuarterRound(s, 1, 5, 9, 13);
            QuarterRound(s, 2, 6, 10, 14);
            QuarterRound(s, 3, 7, 11, 15);
            QuarterRound(s, 0, 5, 10, 15);
            QuarterRound(s, 1, 6, 11, 12);
            QuarterRound(s, 2, 7, 8, 13);
            QuarterRound(s, 3, 4, 9, 14);
        }

        return s[1];
    }

    private static void QuarterRound(Span<uint> s, int a, int b, int c, int d)
    {
        s[a] += s[b];
        s[d] = BitOperations.RotateLeft(s[d] ^ s[a], 16);
        s[c] += s[d];
        s[b] = BitOperations.RotateLeft(s[b] ^ s[c], 12);
        s[a] += s[b];
        s[d] = BitOperations.RotateLeft(s[d] ^ s[a], 8);
        s[c] += s[d];
        s[b] = BitOperations.RotateLeft(s[b] ^ s[c], 7);
    }
}
