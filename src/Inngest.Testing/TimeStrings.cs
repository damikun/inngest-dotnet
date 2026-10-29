// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using System.Globalization;

namespace Inngest.Testing;

// Reads the times the SDK sends Inngest in ops: durations such as "1h30m" and RFC 3339 instants.
internal static class TimeStrings
{
    public static bool TryParseDuration(string? text, out TimeSpan duration)
    {
        duration = TimeSpan.Zero;
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        var ticks = 0L;
        var position = 0;
        while (position < text.Length)
        {
            var start = position;
            while (position < text.Length && char.IsAsciiDigit(text[position]))
            {
                position++;
            }

            if (!long.TryParse(text.AsSpan(start, position - start), NumberStyles.None, CultureInfo.InvariantCulture, out var count))
            {
                return false;
            }

            start = position;
            while (position < text.Length && char.IsAsciiLetter(text[position]))
            {
                position++;
            }

            var unit = text.AsSpan(start, position - start) switch
            {
                "w" => TimeSpan.TicksPerDay * 7,
                "d" => TimeSpan.TicksPerDay,
                "h" => TimeSpan.TicksPerHour,
                "m" => TimeSpan.TicksPerMinute,
                "s" => TimeSpan.TicksPerSecond,
                "ms" => TimeSpan.TicksPerMillisecond,
                _ => 0L,
            };

            if (unit == 0)
            {
                return false;
            }

            ticks += count * unit;
        }

        duration = TimeSpan.FromTicks(ticks);
        return true;
    }

    public static bool TryParseInstant(string? text, out DateTimeOffset instant) =>
        DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out instant);
}
