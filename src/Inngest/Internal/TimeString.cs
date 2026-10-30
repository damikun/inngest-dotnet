// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using System.Globalization;

namespace Inngest.Internal;

// Formats durations as Inngest time strings such as "1h30m". Inngest waits with one-second resolution, so
// sub-second remainders are dropped and positive durations under a second round up to "1s".
internal static class TimeString
{
    private static readonly (char Suffix, long Ticks)[] Units =
    [
        ('w', TimeSpan.TicksPerDay * 7),
        ('d', TimeSpan.TicksPerDay),
        ('h', TimeSpan.TicksPerHour),
        ('m', TimeSpan.TicksPerMinute),
        ('s', TimeSpan.TicksPerSecond),
    ];

    public static string Format(TimeSpan duration) => Format(duration, Units);

    // For a setting Inngest reads with Go's time.ParseDuration, which has no day or week units: a day is "24h".
    public static string FormatInHours(TimeSpan duration) => Format(duration, Units.AsSpan(2));

    private static string Format(TimeSpan duration, ReadOnlySpan<(char Suffix, long Ticks)> units)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(duration, TimeSpan.Zero);

        var remaining = duration.Ticks;
        if (remaining == 0)
        {
            return "0s";
        }

        if (remaining < TimeSpan.TicksPerSecond)
        {
            remaining = TimeSpan.TicksPerSecond;
        }

        Span<char> buffer = stackalloc char[64];
        var length = 0;
        foreach (var (suffix, ticks) in units)
        {
            var count = remaining / ticks;
            if (count == 0)
            {
                continue;
            }

            count.TryFormat(buffer[length..], out var written, provider: CultureInfo.InvariantCulture);
            length += written;
            buffer[length++] = suffix;
            remaining %= ticks;
        }

        return new string(buffer[..length]);
    }

    // Reads what Format writes: whole numbers, each followed by w, d, h, m or s, such as "30s" or "1h30m".
    public static bool TryParse(string? text, out TimeSpan duration)
    {
        duration = TimeSpan.Zero;
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        var total = 0L;
        var position = 0;
        while (position < text.Length)
        {
            var start = position;
            while (position < text.Length && char.IsAsciiDigit(text[position]))
            {
                position++;
            }

            if (position == start || position == text.Length ||
                !long.TryParse(text.AsSpan(start, position - start), NumberStyles.None, CultureInfo.InvariantCulture, out var count))
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

        duration = TimeSpan.FromTicks(total);
        return true;
    }

    // RFC 3339 in UTC with millisecond precision, the form Inngest accepts for absolute times.
    public static string Format(DateTimeOffset instant) =>
        instant.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

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
