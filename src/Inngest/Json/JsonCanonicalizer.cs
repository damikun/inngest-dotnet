// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Inngest.Internal;

namespace Inngest.Json;

// The form Inngest sends every call body in, the JSON Canonicalization Scheme of RFC 8785: the keys of every object
// sorted by their UTF-16 code units, every number read as a 64-bit double and printed the way JavaScript prints it,
// and strings escaped only where JSON requires it. Events and step results therefore reach the SDK in this form,
// whatever wrote them.
internal static class JsonCanonicalizer
{
    // Longer than any number FormatNumber prints, such as -0.0000012345678901234567 or -1.2345678901234567e-308.
    private const int MaxNumberLength = 32;

    // Enough for the 17 significant digits of a double.
    private const int MaxDigits = 17;

    private static readonly byte[] Hex = "0123456789abcdef"u8.ToArray();

    // The value as Inngest sends it back, in an element that owns its memory. A string with a lone surrogate, which
    // Inngest cannot canonicalize either, leaves the value as it is.
    public static JsonElement Canonicalize(JsonElement value, JsonReaderOptions options)
    {
        using var buffer = new PooledBufferWriter();
        try
        {
            Write(value, buffer);
        }
        catch (InvalidOperationException)
        {
            return value.Clone();
        }

        var reader = new Utf8JsonReader(buffer.WrittenSpan, options);
        return JsonElement.ParseValue(ref reader);
    }

    // Throws InvalidOperationException for a string with a lone surrogate.
    public static void Write(JsonElement value, IBufferWriter<byte> output)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                output.Write("{"u8);
                var firstProperty = true;
                foreach (var property in value.EnumerateObject().OrderBy(static property => property.Name, StringComparer.Ordinal))
                {
                    if (!firstProperty)
                    {
                        output.Write(","u8);
                    }

                    firstProperty = false;
                    WriteString(property.Name, output);
                    output.Write(":"u8);
                    Write(property.Value, output);
                }

                output.Write("}"u8);
                break;

            case JsonValueKind.Array:
                output.Write("["u8);
                var firstItem = true;
                foreach (var item in value.EnumerateArray())
                {
                    if (!firstItem)
                    {
                        output.Write(","u8);
                    }

                    firstItem = false;
                    Write(item, output);
                }

                output.Write("]"u8);
                break;

            case JsonValueKind.String:
                WriteString(value.GetString()!, output);
                break;

            case JsonValueKind.Number:
                WriteNumber(value, output);
                break;

            case JsonValueKind.True:
                output.Write("true"u8);
                break;

            case JsonValueKind.False:
                output.Write("false"u8);
                break;

            default:
                output.Write("null"u8);
                break;
        }
    }

    // Prints a finite double the way JavaScript's Number.prototype.toString does: the fewest digits that read back as
    // the same double, in plain notation from 1e-6 up to 1e21 and in exponent notation outside it. Returns the length.
    public static int FormatNumber(double value, Span<byte> destination)
    {
        if (value == 0)
        {
            destination[0] = (byte)'0';
            return 1;
        }

        // .NET picks the same shortest digits; only the notation differs.
        Span<byte> shortest = stackalloc byte[MaxNumberLength];
        Math.Abs(value).TryFormat(shortest, out var written, "R", CultureInfo.InvariantCulture);
        Span<byte> digits = stackalloc byte[MaxDigits];
        TryDecompose(shortest[..written], digits, out var count, out var point);

        var length = 0;
        if (value < 0)
        {
            destination[length++] = (byte)'-';
        }

        if (count <= point && point <= 21)
        {
            Append(destination, ref length, digits[..count]);
            Repeat(destination, ref length, (byte)'0', point - count);
        }
        else if (0 < point && point <= 21)
        {
            Append(destination, ref length, digits[..point]);
            destination[length++] = (byte)'.';
            Append(destination, ref length, digits[point..count]);
        }
        else if (-6 < point && point <= 0)
        {
            Append(destination, ref length, "0."u8);
            Repeat(destination, ref length, (byte)'0', -point);
            Append(destination, ref length, digits[..count]);
        }
        else
        {
            destination[length++] = digits[0];
            if (count > 1)
            {
                destination[length++] = (byte)'.';
                Append(destination, ref length, digits[1..count]);
            }

            destination[length++] = (byte)'e';
            destination[length++] = point - 1 < 0 ? (byte)'-' : (byte)'+';
            Math.Abs(point - 1).TryFormat(destination[length..], out var exponent, provider: CultureInfo.InvariantCulture);
            length += exponent;
        }

        return length;
    }

    // Splits a JSON number into its significant digits, without leading or trailing zeros, and the place of its
    // decimal point: the number is 0.<digits> × 10^point, so 19.90 is 199 and 2, and 0.001 is 1 and -2. Zero has no
    // digits. Returns false when there are more significant digits than fit.
    public static bool TryDecompose(ReadOnlySpan<byte> number, Span<byte> digits, out int count, out int point)
    {
        count = 0;
        point = 0;
        var significant = false;
        var fraction = false;
        var zeros = 0;
        var i = number.Length > 0 && number[0] == '-' ? 1 : 0;
        for (; i < number.Length; i++)
        {
            var c = number[i];
            if (c == '.')
            {
                fraction = true;
                continue;
            }

            if (c is (byte)'e' or (byte)'E')
            {
                break;
            }

            if (!significant)
            {
                if (c == '0')
                {
                    point -= fraction ? 1 : 0;
                    continue;
                }

                significant = true;
            }

            point += fraction ? 0 : 1;

            // Zeros count only once a digit other than zero follows them.
            if (c == '0')
            {
                zeros++;
                continue;
            }

            if (count + zeros + 1 > digits.Length)
            {
                return false;
            }

            digits.Slice(count, zeros).Fill((byte)'0');
            count += zeros;
            zeros = 0;
            digits[count++] = c;
        }

        if (!significant)
        {
            point = 0;
            return true;
        }

        if (i < number.Length)
        {
            point += Exponent(number[(i + 1)..]);
        }

        return true;
    }

    private static void WriteNumber(JsonElement value, IBufferWriter<byte> output)
    {
        // Inngest fails a call whose body holds a number beyond the range of a double, so such a number is left as is.
        var raw = Raw(value);
        if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) || !double.IsFinite(number))
        {
            output.Write(raw);
            return;
        }

        var span = output.GetSpan(MaxNumberLength);
        output.Advance(FormatNumber(number, span));
    }

    private static ReadOnlySpan<byte> Raw(JsonElement number)
    {
#if NET9_0_OR_GREATER
        return System.Runtime.InteropServices.JsonMarshal.GetRawUtf8Value(number);
#else
        return Encoding.UTF8.GetBytes(number.GetRawText());
#endif
    }

    // Escapes the quote, the backslash and the control characters, with the short forms JSON has for some of them,
    // and writes everything else as UTF-8.
    private static void WriteString(string value, IBufferWriter<byte> output)
    {
        var utf8 = Encoding.UTF8.GetBytes(value).AsSpan();
        output.Write("\""u8);
        while (!utf8.IsEmpty)
        {
            var plain = 0;
            while (plain < utf8.Length && utf8[plain] >= 0x20 && utf8[plain] != '"' && utf8[plain] != '\\')
            {
                plain++;
            }

            output.Write(utf8[..plain]);
            if (plain == utf8.Length)
            {
                break;
            }

            var c = utf8[plain];
            switch (c)
            {
                case (byte)'"':
                    output.Write("\\\""u8);
                    break;
                case (byte)'\\':
                    output.Write("\\\\"u8);
                    break;
                case (byte)'\b':
                    output.Write("\\b"u8);
                    break;
                case (byte)'\f':
                    output.Write("\\f"u8);
                    break;
                case (byte)'\n':
                    output.Write("\\n"u8);
                    break;
                case (byte)'\r':
                    output.Write("\\r"u8);
                    break;
                case (byte)'\t':
                    output.Write("\\t"u8);
                    break;
                default:
                    output.Write([(byte)'\\', (byte)'u', (byte)'0', (byte)'0', Hex[c >> 4], Hex[c & 0xF]]);
                    break;
            }

            utf8 = utf8[(plain + 1)..];
        }

        output.Write("\""u8);
    }

    // The exponent of a JSON number, held within a range that keeps the arithmetic on it from overflowing.
    private static int Exponent(ReadOnlySpan<byte> text)
    {
        var negative = false;
        if (!text.IsEmpty && text[0] is (byte)'+' or (byte)'-')
        {
            negative = text[0] == '-';
            text = text[1..];
        }

        var exponent = 0;
        foreach (var c in text)
        {
            exponent = Math.Min(exponent * 10 + (c - '0'), 100_000);
        }

        return negative ? -exponent : exponent;
    }

    private static void Append(Span<byte> destination, ref int length, ReadOnlySpan<byte> bytes)
    {
        bytes.CopyTo(destination[length..]);
        length += bytes.Length;
    }

    private static void Repeat(Span<byte> destination, ref int length, byte value, int count)
    {
        destination.Slice(length, count).Fill(value);
        length += count;
    }
}
