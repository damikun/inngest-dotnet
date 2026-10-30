// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

namespace Inngest;

public static class Trigger
{
    public static EventTrigger Event(string name, string? @if = null) => new(name) { If = @if };

    public static EventTrigger Event<T>(string? @if = null)
        where T : IInngestEventData => new(T.EventName) { If = @if };

    public static CronTrigger Cron(string schedule, string? timezone = null) => new(schedule) { Timezone = timezone };

    public static CronTrigger Cron(string schedule, TimeSpan jitter, string? timezone = null) =>
        new(schedule) { Timezone = timezone, Jitter = jitter };
}
