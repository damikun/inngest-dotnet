// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

namespace Inngest;

// Each attribute here sets the FunctionOptions member of the same name to the record of the same name, which
// FlowControl.cs describes. Durations are time strings such as "30s", "5m" or "1h30m".

// Inngest allows two concurrency limits, so the attribute can be repeated.
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
public sealed class ConcurrencyAttribute(int limit) : Attribute
{
    public int Limit { get; } = limit;

    public string? Key { get; set; }

    public ConcurrencyScope Scope { get; set; } = ConcurrencyScope.Function;
}

[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class ThrottleAttribute(int limit, string period) : Attribute
{
    public int Limit { get; } = limit;

    public string Period { get; } = period;

    public int Burst
    {
        get => BurstValue ?? 0;
        set => BurstValue = value;
    }

    public string? Key { get; set; }

    // An attribute property cannot be nullable, so this tells an unset Burst from 0.
    internal int? BurstValue { get; private set; }
}

[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class RateLimitAttribute(int limit, string period) : Attribute
{
    public int Limit { get; } = limit;

    public string Period { get; } = period;

    public string? Key { get; set; }
}

[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class DebounceAttribute(string period) : Attribute
{
    public string Period { get; } = period;

    public string? Key { get; set; }

    public string? Timeout { get; set; }
}

[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class BatchEventsAttribute(int maxSize, string timeout) : Attribute
{
    public int MaxSize { get; } = maxSize;

    public string Timeout { get; } = timeout;

    public string? Key { get; set; }

    public string? If { get; set; }
}

[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class PriorityAttribute(string run) : Attribute
{
    public string Run { get; } = run;
}

[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class SingletonAttribute(SingletonMode mode) : Attribute
{
    public SingletonMode Mode { get; } = mode;

    public string? Key { get; set; }
}

// Repeat it for every event that cancels the function.
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
public class CancelOnAttribute(string name) : Attribute
{
    public string Event { get; } = name;

    public string? If { get; set; }

    // Named like CancelOn.Match. It hides Attribute.Match, which compares attributes and is of no use here.
    public new string? Match { get; set; }

    public string? Timeout { get; set; }
}

// Cancels the function when an event of type T, named T.EventName, arrives.
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
public sealed class CancelOnAttribute<T>() : CancelOnAttribute(T.EventName)
    where T : IInngestEventData;

[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class TimeoutsAttribute : Attribute
{
    public string? Start { get; set; }

    public string? Finish { get; set; }
}
