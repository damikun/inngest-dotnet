// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

namespace Inngest;

// What starts a function run: a matching event or a cron schedule.
public abstract record FunctionTrigger
{
    private protected FunctionTrigger()
    {
    }
}

// Runs the function for every event with this name, optionally filtered by an expression such as
// "event.data.total > 100".
public sealed record EventTrigger(string Event) : FunctionTrigger
{
    public string? If { get; init; }
}

// Runs the function on a cron schedule. Prefix the schedule with "TZ=Europe/Prague " or set Timezone to
// evaluate it in a time zone other than UTC.
public sealed record CronTrigger(string Schedule) : FunctionTrigger
{
    public string? Timezone { get; init; }

    // Starts each scheduled run up to this much later, picked at random, so that functions on the same schedule do
    // not all start at once. From 1s to 5m.
    public TimeSpan? Jitter { get; init; }
}

public static class Trigger
{
    public static EventTrigger Event(string name, string? @if = null) => new(name) { If = @if };

    public static EventTrigger Event<T>(string? @if = null)
        where T : IInngestEventData => new(T.EventName) { If = @if };

    public static CronTrigger Cron(string schedule, string? timezone = null) => new(schedule) { Timezone = timezone };

    public static CronTrigger Cron(string schedule, TimeSpan jitter, string? timezone = null) =>
        new(schedule) { Timezone = timezone, Jitter = jitter };
}
