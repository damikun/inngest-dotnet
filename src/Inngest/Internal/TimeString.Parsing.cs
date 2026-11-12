// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

namespace Inngest.Internal;

// The units of time strings, and the parser of what the attributes take, such as [Throttle(10, "1m")]. The analyzers
// compile this file too, so that a time string they accept is one the attributes accept, so it targets netstandard2.0
// and uses no using directives, which the SDK's implicit usings would make redundant.
internal static partial class TimeString
{
    private static readonly (char Suffix, long Ticks)[] Units =
    [
        ('w', System.TimeSpan.TicksPerDay * 7),
        ('d', System.TimeSpan.TicksPerDay),
        ('h', System.TimeSpan.TicksPerHour),
        ('m', System.TimeSpan.TicksPerMinute),
        ('s', System.TimeSpan.TicksPerSecond),
    ];

    // Reads what Format writes: whole numbers, each followed by w, d, h, m or s, such as "30s" or "1h30m".
    public static bool TryParse(string? text, out System.TimeSpan duration)
    {
        duration = System.TimeSpan.Zero;
        if (text is null || text.Length == 0)
        {
            return false;
        }

        var total = 0L;
        var position = 0;
        while (position < text.Length)
        {
            var start = position;
            while (position < text.Length && text[position] is >= '0' and <= '9')
            {
                position++;
            }

            if (position == start || position == text.Length ||
                !long.TryParse(text.Substring(start, position - start), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var count))
            {
                return false;
            }

            var unit = UnitTicks(text[position++]);
            if (unit == 0 || count > (long.MaxValue - total) / unit)
            {
                return false;
            }

            total += count * unit;
        }

        duration = System.TimeSpan.FromTicks(total);
        return true;
    }

    private static long UnitTicks(char suffix)
    {
        foreach (var (unit, ticks) in Units)
        {
            if (unit == suffix)
            {
                return ticks;
            }
        }

        return 0;
    }
}
