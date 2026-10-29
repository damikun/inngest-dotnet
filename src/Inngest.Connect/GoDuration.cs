// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using System.Globalization;

namespace Inngest.Connect;

// Reads durations in the form Go's time.Duration.String() writes them, which is how the gateway sends its intervals:
// "10s", "1m30s", "2m0s", "1.5s", "500ms" or "0s". A sequence of numbers with units, optionally signed, as in Go's
// time.ParseDuration.
internal static class GoDuration
{
    public static bool TryParse(string? text, out TimeSpan duration)
    {
        duration = TimeSpan.Zero;
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        var span = text.AsSpan();
        var negative = span[0] == '-';
        if (span[0] is '-' or '+')
        {
            span = span[1..];
        }

        // Go accepts a bare zero without a unit, and nothing else without one.
        if (span is "0")
        {
            return true;
        }

        if (span.IsEmpty)
        {
            return false;
        }

        double ticks = 0;
        while (!span.IsEmpty)
        {
            var numberLength = span.IndexOfAnyExcept("0123456789.");
            if (numberLength <= 0
                || !double.TryParse(span[..numberLength], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var number))
            {
                return false;
            }

            span = span[numberLength..];
            var unitLength = span.IndexOfAny("0123456789.");
            if (unitLength < 0)
            {
                unitLength = span.Length;
            }

            if (UnitTicks(span[..unitLength]) is not { } unit)
            {
                return false;
            }

            ticks += number * unit;
            span = span[unitLength..];
        }

        if (ticks > TimeSpan.MaxValue.Ticks)
        {
            return false;
        }

        duration = TimeSpan.FromTicks((long)Math.Round(negative ? -ticks : ticks));
        return true;
    }

    private static double? UnitTicks(ReadOnlySpan<char> unit) => unit switch
    {
        "ns" => TimeSpan.TicksPerMicrosecond / 1000.0,
        "us" or "µs" or "μs" => TimeSpan.TicksPerMicrosecond,
        "ms" => TimeSpan.TicksPerMillisecond,
        "s" => TimeSpan.TicksPerSecond,
        "m" => TimeSpan.TicksPerMinute,
        "h" => TimeSpan.TicksPerHour,
        _ => null,
    };
}
