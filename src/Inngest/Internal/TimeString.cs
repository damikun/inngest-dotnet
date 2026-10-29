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

    public static string Format(TimeSpan duration)
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
        foreach (var (suffix, ticks) in Units)
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

    // RFC 3339 in UTC with millisecond precision, the form Inngest accepts for absolute times.
    public static string Format(DateTimeOffset instant) =>
        instant.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
}
