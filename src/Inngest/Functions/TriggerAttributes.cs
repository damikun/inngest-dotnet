// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

namespace Inngest;

// Runs the function for every event with this name, optionally filtered by an expression such as
// "event.data.total > 100". A class can have several triggers, of either kind.
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
public class EventTriggerAttribute(string name) : Attribute
{
    public string Event { get; } = name;

    public string? If { get; set; }
}

// Runs the function for every event of type T, named T.EventName.
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
public sealed class EventTriggerAttribute<T>() : EventTriggerAttribute(T.EventName)
    where T : IInngestEventData;

// Runs the function on a cron schedule, in UTC unless Timezone names another time zone, such as "Europe/Prague".
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
public sealed class CronTriggerAttribute(string schedule) : Attribute
{
    public string Schedule { get; } = schedule;

    public string? Timezone { get; set; }

    // Starts each scheduled run up to this much later, from "1s" to "5m", so that functions on the same schedule do
    // not all start at once.
    public string? Jitter { get; set; }
}
