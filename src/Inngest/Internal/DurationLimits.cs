// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.

namespace Inngest.Internal;

// The durations Inngest accepts for each setting of a function, which FunctionOptionsValidator applies at startup and
// the analyzers apply to the time strings of the attributes at build time. It targets netstandard2.0 for the analyzers
// and uses no using directives, which the SDK's implicit usings would make redundant.
internal static class DurationLimits
{
    public const string ThrottlePeriod = "Throttle.Period";
    public const string RateLimitPeriod = "RateLimit.Period";
    public const string DebouncePeriod = "Debounce.Period";
    public const string DebounceTimeout = "Debounce.Timeout";
    public const string BatchEventsTimeout = "BatchEvents.Timeout";
    public const string CancelOnTimeout = "CancelOn.Timeout";
    public const string TimeoutsStart = "Timeouts.Start";
    public const string TimeoutsFinish = "Timeouts.Finish";
    public const string CronJitter = "CronTrigger.Jitter";

    public static readonly System.TimeSpan MinPeriod = System.TimeSpan.FromSeconds(1);
    public static readonly System.TimeSpan MaxDebouncePeriod = System.TimeSpan.FromDays(7);
    public static readonly System.TimeSpan MaxRateLimitPeriod = System.TimeSpan.FromHours(24);
    public static readonly System.TimeSpan MaxCronJitter = System.TimeSpan.FromMinutes(5);

    // What is wrong with the duration of the setting, such as "must be at least 1s", or null when it is allowed. The
    // upper limit of a batch's timeout depends on the Inngest plan, so it is left to the server.
    public static string? FindProblem(string setting, System.TimeSpan value) => setting switch
    {
        ThrottlePeriod or BatchEventsTimeout => value < MinPeriod ? "must be at least 1s" : null,
        RateLimitPeriod => Between(value, MaxRateLimitPeriod, "24h"),
        DebouncePeriod => Between(value, MaxDebouncePeriod, "7d"),
        CronJitter => Between(value, MaxCronJitter, "5m"),
        DebounceTimeout or CancelOnTimeout or TimeoutsStart or TimeoutsFinish => value <= System.TimeSpan.Zero ? "must be greater than zero" : null,
        _ => null,
    };

    private static string? Between(System.TimeSpan value, System.TimeSpan max, string described) =>
        value < MinPeriod || value > max ? $"must be between 1s and {described}" : null;
}
