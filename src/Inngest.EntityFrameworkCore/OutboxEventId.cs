// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.

using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Inngest.EntityFrameworkCore;

// The ids of events in the format Inngest gives its own: a ULID, 48 bits of Unix milliseconds and 80 random bits in 26
// characters of Crockford's base32. Inngest takes any string as an event id, so this is a choice, not a requirement:
// ULIDs are unique without coordination between the instances that add events and sort by the time they were made.
internal static class OutboxEventId
{
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    public static string New(DateTimeOffset now)
    {
        Span<byte> bytes = stackalloc byte[16];
        var milliseconds = (ulong)now.ToUnixTimeMilliseconds();
        for (var i = 5; i >= 0; i--)
        {
            bytes[i] = (byte)milliseconds;
            milliseconds >>= 8;
        }

        RandomNumberGenerator.Fill(bytes[6..]);

        var value = new UInt128(BinaryPrimitives.ReadUInt64BigEndian(bytes), BinaryPrimitives.ReadUInt64BigEndian(bytes[8..]));
        Span<char> id = stackalloc char[26];
        for (var i = id.Length - 1; i >= 0; i--)
        {
            id[i] = Alphabet[(int)(value & 31)];
            value >>= 5;
        }

        return new string(id);
    }
}
