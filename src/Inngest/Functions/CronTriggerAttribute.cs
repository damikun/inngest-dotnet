// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

namespace Inngest;

/// <summary>
/// Runs the function on a cron schedule, in UTC unless Timezone names another time zone, such as "Europe/Prague".
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
public sealed class CronTriggerAttribute(string schedule) : Attribute
{
    public string Schedule { get; } = schedule;

    public string? Timezone { get; set; }

    /// <summary>
    /// Starts each scheduled run up to this much later, from "1s" to "5m", so that functions on the same schedule do
    /// not all start at once.
    /// </summary>
    public string? Jitter { get; set; }
}
