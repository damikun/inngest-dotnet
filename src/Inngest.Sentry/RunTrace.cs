// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Sentry;

namespace Inngest.Sentry;

// The trace all calls of a run report to, whichever instance of the app they reach. A run id is a ULID, which holds 128
// bits, as a trace id does, so the trace id is the run id, mapped the way the TypeScript middleware maps it; runs that
// both SDKs report land in one trace. An id that is no ULID gets the first 16 bytes of its hash instead.
internal static class RunTrace
{
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    // The span the trace continues from, which never existed. The TypeScript middleware uses the same one.
    private static readonly SpanId Origin = SpanId.Parse("1000000000000000");

    public static SentryTraceHeader HeaderOf(string runId) => new(IdOf(runId), Origin, isSampled: null);

    public static SentryId IdOf(string runId)
    {
        Span<byte> bytes = stackalloc byte[16];
        if (!TryDecode(runId, bytes))
        {
            Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
            SHA256.HashData(Encoding.UTF8.GetBytes(runId), hash);
            hash[..bytes.Length].CopyTo(bytes);
        }

        return new SentryId(new Guid(bytes, bigEndian: true));
    }

    // 26 characters of Crockford's base32 are 130 bits; the first one holds only its low three.
    private static bool TryDecode(string ulid, Span<byte> bytes)
    {
        if (ulid.Length != 26)
        {
            return false;
        }

        UInt128 value = 0;
        for (var i = 0; i < ulid.Length; i++)
        {
            var digit = Alphabet.IndexOf(char.ToUpperInvariant(ulid[i]));
            if (digit < 0)
            {
                return false;
            }

            value = (value << 5) | (uint)(i == 0 ? digit & 0b111 : digit);
        }

        BinaryPrimitives.WriteUInt128BigEndian(bytes, value);
        return true;
    }
}
