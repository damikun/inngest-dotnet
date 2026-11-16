// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Unicode;
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

    // Any number of up to 15 significant digits reads back from a double as it was written.
    private const int MaxExactDigits = 15;

    private static readonly byte[] Hex = "0123456789abcdef"u8.ToArray();

    // The value as Inngest sends it back, in an element that owns its memory. A value in that form already is only
    // parsed. A string with a lone surrogate, which Inngest cannot canonicalize either, or with bytes that are not
    // UTF-8 leaves the value as it is.
    public static JsonElement Canonicalize(ReadOnlySpan<byte> json, JsonReaderOptions options)
    {
        PooledBufferWriter? buffer = null;
        try
        {
            var document = new Document(json);
            try
            {
                if (!document.Read(options) && Utf8.IsValid(json))
                {
                    buffer = new PooledBufferWriter(json.Length);
                    document.Write(buffer);
                    json = buffer.WrittenSpan;
                }
            }
            catch (InvalidOperationException)
            {
                // A lone surrogate: the value is parsed as it was written.
            }
            finally
            {
                document.Dispose();
            }

            var reader = new Utf8JsonReader(json, options);
            return JsonElement.ParseValue(ref reader);
        }
        finally
        {
            buffer?.Dispose();
        }
    }

    // Throws InvalidOperationException for a string with a lone surrogate, or with bytes that are not UTF-8 when
    // anything else has to be rewritten.
    public static void Write(ReadOnlySpan<byte> json, IBufferWriter<byte> output, JsonReaderOptions options = default)
    {
        var document = new Document(json);
        try
        {
            if (document.Read(options))
            {
                output.Write(json);
                return;
            }

            if (!Utf8.IsValid(json))
            {
                throw new InvalidOperationException("The JSON has a string that is not UTF-8.");
            }

            document.Write(output);
        }
        finally
        {
            document.Dispose();
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

    // Compares two keys, as they are written between their quotes, by their UTF-16 code units.
    private static int CompareKeys(ReadOnlySpan<byte> left, bool leftEscaped, ReadOnlySpan<byte> right, bool rightEscaped)
    {
        if (!leftEscaped && !rightEscaped)
        {
            var common = left.CommonPrefixLength(right);
            if (common == left.Length || common == right.Length)
            {
                return left.Length - right.Length;
            }

            // UTF-8 sorts as code points do, which puts U+E000 to U+FFFF before the characters beyond U+FFFF. UTF-16
            // puts those characters first, as their surrogates come before U+E000. The first byte of a character tells.
            int a = left[common], b = right[common];
            return a >= 0xEE && b >= 0xEE && (a >= 0xF0) != (b >= 0xF0) ? b - a : a - b;
        }

        int i = 0, j = 0;
        while (i < left.Length && j < right.Length)
        {
            var difference = SortWeight(NextCodePoint(left, ref i)) - SortWeight(NextCodePoint(right, ref j));
            if (difference != 0)
            {
                return difference;
            }
        }

        return (i < left.Length ? 1 : 0) - (j < right.Length ? 1 : 0);
    }

    // Code points weighed so that they sort as their UTF-16 code units do.
    private static int SortWeight(int codePoint) => codePoint is >= 0xD800 and <= 0xFFFF ? codePoint + 0x110000 : codePoint;

    private static int NextCodePoint(ReadOnlySpan<byte> text, ref int index)
    {
        var c = text[index];
        if (c == '\\')
        {
            var escape = text[index + 1];
            if (escape == 'u')
            {
                index += ReadUnicodeEscape(text[index..], out var codePoint);
                return codePoint;
            }

            index += 2;
            return escape switch
            {
                (byte)'b' => '\b',
                (byte)'f' => '\f',
                (byte)'n' => '\n',
                (byte)'r' => '\r',
                (byte)'t' => '\t',
                _ => escape,
            };
        }

        if (c < 0x80)
        {
            index++;
            return c;
        }

        Rune.DecodeFromUtf8(text[index..], out var rune, out var consumed);
        index += consumed;
        return rune.Value;
    }

    // Reads the \u escape at the start of text, and the one after it when the two make a surrogate pair. Returns the
    // length read; a lone surrogate comes back as it is.
    private static int ReadUnicodeEscape(ReadOnlySpan<byte> text, out int codePoint)
    {
        codePoint = HexValue(text.Slice(2, 4));
        if (char.IsHighSurrogate((char)codePoint) && text.Length >= 12 && text[6] == '\\' && text[7] == 'u')
        {
            var low = HexValue(text.Slice(8, 4));
            if (char.IsLowSurrogate((char)low))
            {
                codePoint = char.ConvertToUtf32((char)codePoint, (char)low);
                return 12;
            }
        }

        return 6;
    }

    private static int HexValue(ReadOnlySpan<byte> digits)
    {
        var value = 0;
        foreach (var digit in digits)
        {
            value = (value << 4) | (digit <= '9' ? digit - '0' : (digit | 0x20) - 'a' + 10);
        }

        return value;
    }

    // Whether WriteNumber writes a number as it is.
    private static bool IsCanonicalNumber(ReadOnlySpan<byte> number)
    {
        if (IsPlainAndExact(number))
        {
            return true;
        }

        Span<byte> canonical = stackalloc byte[MaxNumberLength];
        return !TryFormatCanonical(number, canonical, out var length) || canonical[..length].SequenceEqual(number);
    }

    // Most numbers print as they are written, which this tells without going through a double: up to 15 significant
    // digits, which a double keeps, no exponent, no zeros at the end of a fraction, not -0, and in the range JavaScript
    // prints without an exponent.
    private static bool IsPlainAndExact(ReadOnlySpan<byte> number)
    {
        if (number.IndexOfAny((byte)'e', (byte)'E') >= 0 || (number[^1] == '0' && number.Contains((byte)'.')))
        {
            return false;
        }

        Span<byte> digits = stackalloc byte[MaxDigits];
        return TryDecompose(number, digits, out var count, out var point)
            && count <= MaxExactDigits
            && (count == 0 ? number[0] != '-' : point is > -6 and <= 21);
    }

    // Inngest fails a call whose body holds a number beyond the range of a double, so such a number is left as is.
    private static bool TryFormatCanonical(ReadOnlySpan<byte> number, Span<byte> destination, out int length)
    {
        if (!double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || !double.IsFinite(value))
        {
            length = 0;
            return false;
        }

        length = FormatNumber(value, destination);
        return true;
    }

    private static void WriteNumber(ReadOnlySpan<byte> number, IBufferWriter<byte> output)
    {
        var span = output.GetSpan(MaxNumberLength);
        if (IsPlainAndExact(number) || !TryFormatCanonical(number, span, out var length))
        {
            output.Write(number);
            return;
        }

        output.Advance(length);
    }

    // Whether every escape in a string is the one WriteString writes: the short forms JSON has for the quote, the
    // backslash and some control characters, and \u00xx for the other control characters.
    private static bool HasCanonicalEscapes(ReadOnlySpan<byte> text)
    {
        for (var i = text.IndexOf((byte)'\\'); i >= 0; i = text.IndexOf((byte)'\\'))
        {
            text = text[i..];
            switch (text[1])
            {
                case (byte)'"' or (byte)'\\' or (byte)'b' or (byte)'f' or (byte)'n' or (byte)'r' or (byte)'t':
                    text = text[2..];
                    break;
                case (byte)'u' when text[2] == '0' && text[3] == '0' && text[4] is (byte)'0' or (byte)'1'
                    && text[5] is >= (byte)'0' and <= (byte)'9' or >= (byte)'a' and <= (byte)'f'
                    && ShortEscape(HexValue(text.Slice(4, 2))) == 0:
                    text = text[6..];
                    break;
                default:
                    return false;
            }
        }

        return true;
    }

    private static byte ShortEscape(int c) => c switch
    {
        '\b' => (byte)'b',
        '\f' => (byte)'f',
        '\n' => (byte)'n',
        '\r' => (byte)'r',
        '\t' => (byte)'t',
        '"' => (byte)'"',
        '\\' => (byte)'\\',
        _ => 0,
    };

    // Writes a string from its text between the quotes. Text between escapes is copied as it is, and each escape
    // becomes the character, escaped only where JSON requires it.
    private static void WriteString(ReadOnlySpan<byte> text, bool escaped, IBufferWriter<byte> output)
    {
        output.Write("\""u8);
        if (escaped)
        {
            for (var i = text.IndexOf((byte)'\\'); i >= 0; i = text.IndexOf((byte)'\\'))
            {
                output.Write(text[..i]);
                text = text[i..];
                switch (text[1])
                {
                    case (byte)'u':
                        text = text[ReadUnicodeEscape(text, out var codePoint)..];
                        WriteCharacter(codePoint, output);
                        break;
                    case (byte)'/':
                        output.Write("/"u8);
                        text = text[2..];
                        break;
                    default:
                        output.Write(text[..2]);
                        text = text[2..];
                        break;
                }
            }
        }

        output.Write(text);
        output.Write("\""u8);
    }

    private static void WriteCharacter(int codePoint, IBufferWriter<byte> output)
    {
        if (ShortEscape(codePoint) is not 0 and var shortEscape)
        {
            output.Write([(byte)'\\', shortEscape]);
        }
        else if (codePoint < 0x20)
        {
            output.Write([(byte)'\\', (byte)'u', (byte)'0', (byte)'0', Hex[codePoint >> 4], Hex[codePoint & 0xF]]);
        }
        else if (Rune.TryCreate(codePoint, out var rune))
        {
            output.Advance(rune.EncodeToUtf8(output.GetSpan(4)));
        }
        else
        {
            throw new InvalidOperationException("The JSON has a string with a lone surrogate.");
        }
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

    // A value read once into a flat list of its tokens, so that the members of an object can be written in another
    // order. The lists come from ArrayPool.
    private ref struct Document
    {
        private const int InsertionSortLength = 8;

        private readonly ReadOnlySpan<byte> _json;
        private Token[] _tokens;
        private int _count;

        // For each open container, its token and, in an object, the last key read so far.
        private int[] _open;

        public Document(ReadOnlySpan<byte> json)
        {
            _json = json;
            _tokens = ArrayPool<Token>.Shared.Rent(json.Length / 8 + 16);
            _open = ArrayPool<int>.Shared.Rent(64);
        }

        // Reads the value and returns whether it is in canonical form already: without whitespace, with the keys of
        // every object in order, and every string and number written as WriteValue writes it.
        public bool Read(JsonReaderOptions options)
        {
            var reader = new Utf8JsonReader(_json, options);
            var canonical = true;
            var depth = 0;
            var next = 0;
            var previous = JsonTokenType.None;
            while (reader.Read())
            {
                var type = reader.TokenType;
                var start = (int)reader.TokenStartIndex;

                // A comma or a colon comes before every token but the first in a container and the closing brackets.
                var separated = previous is not (JsonTokenType.None or JsonTokenType.StartObject or JsonTokenType.StartArray)
                    && type is not (JsonTokenType.EndObject or JsonTokenType.EndArray);
                canonical &= start == next + (separated ? 1 : 0);
                previous = type;

                if (type is JsonTokenType.EndObject or JsonTokenType.EndArray)
                {
                    depth--;
                    _tokens[_open[depth * 2]].End = _count;
                    next = start + 1;
                    continue;
                }

                if (_count == _tokens.Length)
                {
                    Grow(ref _tokens, _count);
                }

                ref var token = ref _tokens[_count];
                token = new Token { Type = type, Start = start, Sorted = true };
                switch (type)
                {
                    case JsonTokenType.StartObject or JsonTokenType.StartArray:
                        if ((depth + 1) * 2 > _open.Length)
                        {
                            Grow(ref _open, depth * 2);
                        }

                        _open[depth * 2] = _count;
                        _open[(depth * 2) + 1] = -1;
                        depth++;
                        next = start + 1;
                        break;

                    case JsonTokenType.PropertyName or JsonTokenType.String:
                        var text = reader.ValueSpan;
                        token.Start = start + 1;
                        token.Length = text.Length;
                        token.Escaped = reader.ValueIsEscaped;
                        canonical &= !token.Escaped || HasCanonicalEscapes(text);
                        next = start + text.Length + 2;
                        if (type == JsonTokenType.PropertyName)
                        {
                            ref var parent = ref _tokens[_open[(depth - 1) * 2]];
                            ref var last = ref _open[((depth - 1) * 2) + 1];
                            if (parent.Sorted && last >= 0 && Compare(last, _count) > 0)
                            {
                                parent.Sorted = false;
                                canonical = false;
                            }

                            last = _count;
                        }

                        break;

                    case JsonTokenType.Number:
                        token.Length = reader.ValueSpan.Length;
                        canonical &= IsCanonicalNumber(reader.ValueSpan);
                        next = start + token.Length;
                        break;

                    default:
                        token.Length = reader.ValueSpan.Length;
                        next = start + token.Length;
                        break;
                }

                _count++;
            }

            return canonical && next == _json.Length;
        }

        public readonly void Write(IBufferWriter<byte> output) => WriteValue(0, output);

        public void Dispose()
        {
            ArrayPool<Token>.Shared.Return(_tokens);
            ArrayPool<int>.Shared.Return(_open);
            _tokens = [];
            _open = [];
        }

        // Writes the value that starts at a token and returns the token after it.
        private readonly int WriteValue(int index, IBufferWriter<byte> output)
        {
            var token = _tokens[index];
            switch (token.Type)
            {
                case JsonTokenType.StartObject:
                    output.Write("{"u8);
                    if (token.Sorted)
                    {
                        for (var key = index + 1; key < token.End;)
                        {
                            key = WriteMember(key, key > index + 1, output);
                        }
                    }
                    else
                    {
                        WriteSorted(index, output);
                    }

                    output.Write("}"u8);
                    return token.End;

                case JsonTokenType.StartArray:
                    output.Write("["u8);
                    for (var item = index + 1; item < token.End;)
                    {
                        if (item > index + 1)
                        {
                            output.Write(","u8);
                        }

                        item = WriteValue(item, output);
                    }

                    output.Write("]"u8);
                    return token.End;

                case JsonTokenType.String:
                    WriteString(_json.Slice(token.Start, token.Length), token.Escaped, output);
                    return index + 1;

                case JsonTokenType.Number:
                    WriteNumber(_json.Slice(token.Start, token.Length), output);
                    return index + 1;

                default:
                    output.Write(_json.Slice(token.Start, token.Length));
                    return index + 1;
            }
        }

        // Writes the key at a token and its value, and returns the token after them.
        private readonly int WriteMember(int key, bool comma, IBufferWriter<byte> output)
        {
            if (comma)
            {
                output.Write(","u8);
            }

            var token = _tokens[key];
            WriteString(_json.Slice(token.Start, token.Length), token.Escaped, output);
            output.Write(":"u8);
            return WriteValue(key + 1, output);
        }

        private readonly void WriteSorted(int index, IBufferWriter<byte> output)
        {
            var end = _tokens[index].End;
            var count = 0;
            for (var key = index + 1; key < end; key = After(key + 1))
            {
                count++;
            }

            var keys = ArrayPool<int>.Shared.Rent(count * 2);
            try
            {
                var key = index + 1;
                for (var i = 0; i < count; i++, key = After(key + 1))
                {
                    keys[i] = key;
                }

                Sort(keys.AsSpan(0, count), keys.AsSpan(count, count));
                for (var i = 0; i < count; i++)
                {
                    WriteMember(keys[i], i > 0, output);
                }
            }
            finally
            {
                ArrayPool<int>.Shared.Return(keys);
            }
        }

        private readonly int After(int index) =>
            _tokens[index].Type is JsonTokenType.StartObject or JsonTokenType.StartArray ? _tokens[index].End : index + 1;

        // A merge sort, so that keys that compare equal stay in the order they came in.
        private readonly void Sort(Span<int> keys, Span<int> scratch)
        {
            if (keys.Length <= InsertionSortLength)
            {
                for (var i = 1; i < keys.Length; i++)
                {
                    var key = keys[i];
                    var j = i;
                    for (; j > 0 && Compare(keys[j - 1], key) > 0; j--)
                    {
                        keys[j] = keys[j - 1];
                    }

                    keys[j] = key;
                }

                return;
            }

            var half = keys.Length / 2;
            Sort(keys[..half], scratch[..half]);
            Sort(keys[half..], scratch[half..]);
            if (Compare(keys[half - 1], keys[half]) <= 0)
            {
                return;
            }

            keys.CopyTo(scratch);
            int left = 0, right = half, k = 0;
            while (left < half && right < keys.Length)
            {
                keys[k++] = Compare(scratch[left], scratch[right]) <= 0 ? scratch[left++] : scratch[right++];
            }

            // Keys left over on the right are in place already.
            scratch[left..half].CopyTo(keys[k..]);
        }

        private readonly int Compare(int left, int right)
        {
            var a = _tokens[left];
            var b = _tokens[right];
            return CompareKeys(_json.Slice(a.Start, a.Length), a.Escaped, _json.Slice(b.Start, b.Length), b.Escaped);
        }

        private static void Grow<T>(ref T[] array, int used)
        {
            var larger = ArrayPool<T>.Shared.Rent(array.Length * 2);
            array.AsSpan(0, used).CopyTo(larger);
            ArrayPool<T>.Shared.Return(array);
            array = larger;
        }
    }

    private struct Token
    {
        public JsonTokenType Type;
        public bool Escaped;

        // Whether the keys of an object are in order.
        public bool Sorted;

        // Where the token's text starts, after the quote for a string.
        public int Start;
        public int Length;

        // For a container, the token after its last one.
        public int End;
    }
}
