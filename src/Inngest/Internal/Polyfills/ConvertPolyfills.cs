// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

#if !NET9_0_OR_GREATER
using System.Buffers;

namespace System;

// The hex methods that Convert gained in .NET 9, compiled for .NET 8 only so that call sites need no conditionals. After
// invalid data, charsConsumed counts whole pairs.
internal static class ConvertPolyfills
{
    extension(Convert)
    {
        public static string ToHexStringLower(ReadOnlySpan<byte> value)
        {
            const string digits = "0123456789abcdef";
            var length = value.Length * 2;
            Span<char> chars = length <= 128 ? stackalloc char[128] : new char[length];
            for (var i = 0; i < value.Length; i++)
            {
                chars[2 * i] = digits[value[i] >> 4];
                chars[2 * i + 1] = digits[value[i] & 0xF];
            }

            return new string(chars[..length]);
        }

        public static OperationStatus FromHexString(ReadOnlySpan<char> source, Span<byte> destination, out int charsConsumed, out int bytesWritten)
        {
            var pairs = source.Length / 2;
            var count = Math.Min(pairs, destination.Length);
            var decoded = 0;
            for (; decoded < count; decoded++)
            {
                var high = HexValue(source[2 * decoded]);
                var low = HexValue(source[2 * decoded + 1]);
                if ((high | low) < 0)
                {
                    break;
                }

                destination[decoded] = (byte)((high << 4) | low);
            }

            charsConsumed = 2 * decoded;
            bytesWritten = decoded;
            return decoded < count ? OperationStatus.InvalidData
                : count < pairs ? OperationStatus.DestinationTooSmall
                : source.Length % 2 == 0 ? OperationStatus.Done
                : OperationStatus.NeedMoreData;
        }
    }

    private static int HexValue(char c) => c switch
    {
        >= '0' and <= '9' => c - '0',
        >= 'a' and <= 'f' => c - 'a' + 10,
        >= 'A' and <= 'F' => c - 'A' + 10,
        _ => -1,
    };
}
#endif
