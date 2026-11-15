// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Inngest.Json;

// Numbers that Inngest does not hand back as they were written. It keeps every number as a 64-bit double and prints
// it with the fewest digits that read back as that double, so a long beyond 2^53, a decimal with more than 15 to 17
// significant digits, and the trailing zeros of a decimal (19.90 comes back as 19.9) all change between calls.
internal static class NumberPrecision
{
    // Up to this many digits, a number without an exponent comes back with the same value.
    private const int ExactDigits = 15;

    // The JSON path of the first number in the value that Inngest hands back changed, or null when there is none.
    public static string? FindChanged(ReadOnlySpan<byte> json, int maxDepth)
    {
        var reader = new Utf8JsonReader(json, new JsonReaderOptions { MaxDepth = maxDepth });
        try
        {
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.Number && Changes(reader.ValueSpan))
                {
                    return PathTo(json, reader.TokenStartIndex, maxDepth);
                }
            }
        }
        catch (JsonException)
        {
            // Not JSON this reader takes, which the caller wrote with its own limits; there is nothing to warn about.
        }

        return null;
    }

    public static bool Changes(ReadOnlySpan<byte> number)
    {
        var digits = 0;
        var fraction = false;
        var exponent = false;
        foreach (var c in number)
        {
            if (c is >= (byte)'0' and <= (byte)'9')
            {
                digits++;
            }
            else if (c == '.')
            {
                fraction = true;
            }
            else if (c is (byte)'e' or (byte)'E')
            {
                exponent = true;
            }
        }

        // Trailing zeros after the decimal point are dropped, so a decimal reads back with a smaller scale.
        if (fraction && !exponent && number[^1] == '0')
        {
            return true;
        }

        if (!exponent && digits <= ExactDigits)
        {
            return false;
        }

        if (!double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || !double.IsFinite(value))
        {
            return true;
        }

        Span<byte> canonical = stackalloc byte[32];
        var length = JsonCanonicalizer.FormatNumber(value, canonical);
        return !SameValue(number, canonical[..length]);
    }

    // Compares the decimal values the two numbers write, not their text: 1e21 and 1E+21 are the same number.
    private static bool SameValue(ReadOnlySpan<byte> written, ReadOnlySpan<byte> canonical)
    {
        Span<byte> left = stackalloc byte[17];
        Span<byte> right = stackalloc byte[17];

        // More significant digits than a double has never come back the same.
        if (!JsonCanonicalizer.TryDecompose(written, left, out var leftCount, out var leftPoint)
            || !JsonCanonicalizer.TryDecompose(canonical, right, out var rightCount, out var rightPoint))
        {
            return false;
        }

        if (leftCount == 0 || rightCount == 0)
        {
            return leftCount == rightCount;
        }

        return (written[0] == '-') == (canonical[0] == '-')
            && leftPoint == rightPoint
            && left[..leftCount].SequenceEqual(right[..rightCount]);
    }

    // Reads the JSON again up to the token at the position, keeping track of where it is, such as $.orders[1].id.
    private static string PathTo(ReadOnlySpan<byte> json, long position, int maxDepth)
    {
        var reader = new Utf8JsonReader(json, new JsonReaderOptions { MaxDepth = maxDepth });
        var frames = new List<(bool Array, int Index, string? Name)>();
        while (reader.Read())
        {
            var token = reader.TokenType;
            if (token == JsonTokenType.PropertyName)
            {
                frames[^1] = (false, 0, reader.GetString());
                continue;
            }

            if (token is JsonTokenType.EndObject or JsonTokenType.EndArray)
            {
                frames.RemoveAt(frames.Count - 1);
                continue;
            }

            if (frames.Count > 0 && frames[^1].Array)
            {
                frames[^1] = (true, frames[^1].Index + 1, null);
            }

            if (reader.TokenStartIndex == position)
            {
                break;
            }

            if (token is JsonTokenType.StartObject or JsonTokenType.StartArray)
            {
                frames.Add((token == JsonTokenType.StartArray, -1, null));
            }
        }

        var path = new StringBuilder("$");
        foreach (var (array, index, name) in frames)
        {
            if (array)
            {
                path.Append('[').Append(index.ToString(CultureInfo.InvariantCulture)).Append(']');
            }
            else if (IsIdentifier(name!))
            {
                path.Append('.').Append(name);
            }
            else
            {
                path.Append("['").Append(name!.Replace("'", "\\'", StringComparison.Ordinal)).Append("']");
            }
        }

        return path.ToString();
    }

    private static bool IsIdentifier(string name)
    {
        if (name.Length == 0 || char.IsAsciiDigit(name[0]))
        {
            return false;
        }

        foreach (var c in name)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c != '_' && c != '$')
            {
                return false;
            }
        }

        return true;
    }
}
