// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.

namespace Inngest.Internal;

// Checks a cron schedule the way Inngest does when an app syncs: pkg/inngest/trigger.go (v1.52.0) parses it with
// github.com/robfig/cron/v3 and the parser for five fields (minute, hour, day of month, month, day of week), without
// descriptors such as @daily, and refuses a schedule longer than 255 characters. A "TZ=" or "CRON_TZ=" prefix names the
// time zone. Whether the zone exists is left to the server, since the zones a machine knows depend on its system.
//
// The analyzers compile this file too, to check the schedules of [CronTrigger] at build time, so it targets
// netstandard2.0 and uses no using directives, which the SDK's implicit usings would make redundant.
internal static class CronExpression
{
    public const int MaxLength = 255;

    private static readonly Field[] Fields =
    [
        new("minute", 0, 59, null),
        new("hour", 0, 23, null),
        new("day of month", 1, 31, null),
        new("month", 1, 12, ["jan", "feb", "mar", "apr", "may", "jun", "jul", "aug", "sep", "oct", "nov", "dec"]),
        new("day of week", 0, 6, ["sun", "mon", "tue", "wed", "thu", "fri", "sat"]),
    ];

    // The schedule the SDK sends Inngest for a trigger: the time zone goes in front, unless the schedule names one.
    public static string WithTimezone(string schedule, string? timezone) =>
        timezone is null || string.IsNullOrWhiteSpace(timezone) || HasTimezone(schedule) ? schedule : $"TZ={timezone} {schedule}";

    // What is wrong with the schedule, such as "the hour 24 is above 23", or null when Inngest accepts it.
    public static string? FindProblem(string schedule)
    {
        if (schedule.Length == 0)
        {
            return "it is empty";
        }

        if (schedule.Length > MaxLength)
        {
            return $"it is longer than {MaxLength} characters";
        }

        var spec = schedule;
        if (HasTimezone(spec))
        {
            var space = spec.IndexOf(' ');
            if (space < 0)
            {
                return "the time zone is not followed by a space and the fields";
            }

            spec = spec.Substring(space).Trim();
        }

        if (spec.StartsWith("@", System.StringComparison.Ordinal))
        {
            return $"Inngest does not accept descriptors such as {spec.Split(' ')[0]}; write the five fields instead";
        }

        var fields = spec.Split((char[]?)null, System.StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length != Fields.Length)
        {
            return $"it has {fields.Length} field{(fields.Length == 1 ? string.Empty : "s")}, and Inngest expects 5: minute, hour, day of month, month and day of week";
        }

        for (var i = 0; i < fields.Length; i++)
        {
            if (FindProblem(fields[i], Fields[i]) is { } problem)
            {
                return problem;
            }
        }

        return null;
    }

    private static bool HasTimezone(string schedule) =>
        schedule.StartsWith("TZ=", System.StringComparison.Ordinal) || schedule.StartsWith("CRON_TZ=", System.StringComparison.Ordinal);

    // A field is a list of ranges separated by commas, where empty items are skipped as robfig/cron skips them.
    private static string? FindProblem(string text, Field field)
    {
        foreach (var range in text.Split([','], System.StringSplitOptions.RemoveEmptyEntries))
        {
            if (FindRangeProblem(range, field) is { } problem)
            {
                return problem;
            }
        }

        return null;
    }

    // "*", "?", "5", "1-5", "*/15", "5/15" (from 5 to the end in steps of 15) or "1-30/2", with names for months and days.
    private static string? FindRangeProblem(string range, Field field)
    {
        var rangeAndStep = range.Split('/');
        var lowAndHigh = rangeAndStep[0].Split('-');
        long start;
        long end;
        if (lowAndHigh[0] is "*" or "?")
        {
            start = field.Min;
            end = field.Max;
        }
        else
        {
            if (Value(lowAndHigh[0], field) is not { } low)
            {
                return NotANumber(lowAndHigh[0], field);
            }

            start = low;
            switch (lowAndHigh.Length)
            {
                case 1:
                    end = start;
                    break;
                case 2:
                    if (Value(lowAndHigh[1], field) is not { } high)
                    {
                        return NotANumber(lowAndHigh[1], field);
                    }

                    end = high;
                    break;
                default:
                    return $"the {field.Name} \"{range}\" has more than one hyphen";
            }
        }

        switch (rangeAndStep.Length)
        {
            case 1:
                break;
            case 2:
                if (Number(rangeAndStep[1]) is not { } step)
                {
                    return $"the step \"{rangeAndStep[1]}\" of the {field.Name} is not a whole number";
                }

                if (step == 0)
                {
                    return $"the {field.Name} \"{range}\" has a step of 0";
                }

                // "5/15" means from 5 to the end of the field.
                if (lowAndHigh.Length == 1)
                {
                    end = field.Max;
                }

                break;
            default:
                return $"the {field.Name} \"{range}\" has more than one slash";
        }

        if (start < field.Min)
        {
            return $"the {field.Name} {start} is below {field.Min}";
        }

        if (end > field.Max)
        {
            return $"the {field.Name} {end} is above {field.Max}";
        }

        return start > end ? $"the {field.Name} range \"{range}\" starts after it ends" : null;
    }

    private static string NotANumber(string text, Field field) =>
        field.Names is null
            ? $"the {field.Name} \"{text}\" is not a whole number"
            : $"the {field.Name} \"{text}\" is not a whole number or a name such as {field.Names[0]}";

    private static long? Value(string text, Field field)
    {
        if (field.Names is { } names)
        {
            var index = System.Array.IndexOf(names, text.ToLowerInvariant());
            if (index >= 0)
            {
                return field.Min + index;
            }
        }

        return Number(text);
    }

    // A number as Go's strconv.Atoi reads it, which takes a sign, and that is not negative.
    private static long? Number(string text) =>
        long.TryParse(text, System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out var number) && number >= 0
            ? number
            : null;

    private sealed class Field(string name, int min, int max, string[]? names)
    {
        public string Name { get; } = name;

        public int Min { get; } = min;

        public int Max { get; } = max;

        public string[]? Names { get; } = names;
    }
}
