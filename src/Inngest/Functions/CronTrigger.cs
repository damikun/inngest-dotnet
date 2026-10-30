// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

namespace Inngest;

/// <summary>
/// Runs the function on a cron schedule. Prefix the schedule with "TZ=Europe/Prague " or set Timezone to
/// evaluate it in a time zone other than UTC.
/// </summary>
public sealed record CronTrigger(string Schedule) : FunctionTrigger
{
    public string? Timezone { get; init; }

    /// <summary>
    /// Starts each scheduled run up to this much later, picked at random, so that functions on the same schedule do
    /// not all start at once. From 1s to 5m.
    /// </summary>
    public TimeSpan? Jitter { get; init; }
}
