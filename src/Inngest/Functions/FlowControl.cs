// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

namespace Inngest;

public enum ConcurrencyScope
{
    Function,
    Environment,
    Account,
}

// Limits how many steps of matching runs execute at once. Key is an expression that splits the limit into
// virtual queues, for example "event.data.accountId".
public sealed record ConcurrencyLimit(int Limit)
{
    public string? Key { get; init; }

    public ConcurrencyScope Scope { get; init; } = ConcurrencyScope.Function;
}

// Queues runs beyond Limit per Period instead of dropping them.
public sealed record Throttle(int Limit, TimeSpan Period)
{
    public int? Burst { get; init; }

    public string? Key { get; init; }
}

// Drops runs beyond Limit per Period.
public sealed record RateLimit(int Limit, TimeSpan Period)
{
    public string? Key { get; init; }
}

// Delays a run until no new matching event arrives for Period, then runs once with the last event.
public sealed record Debounce(TimeSpan Period)
{
    public string? Key { get; init; }

    // Upper bound on how long a run can be delayed by a stream of events.
    public TimeSpan? Timeout { get; init; }
}

// Runs the function with up to MaxSize events at once, or with whatever arrived once Timeout passes.
public sealed record BatchEvents(int MaxSize, TimeSpan Timeout)
{
    public string? Key { get; init; }

    public string? If { get; init; }
}

// Run is an expression returning a number of seconds between -600 and 600 that moves a run forward or back
// in the queue.
public sealed record Priority(string Run);

// Cancels a running function when a matching event arrives. Match is a property path compared between the
// triggering event and the cancelling one, such as "data.userId".
public sealed record CancelOn(string Event)
{
    public string? If { get; init; }

    public string? Match { get; init; }

    public TimeSpan? Timeout { get; init; }
}

// Start limits the time a run may wait in the queue before its first step; Finish limits the time from the
// first step to completion. Runs that exceed either are cancelled.
public sealed record FunctionTimeouts
{
    public TimeSpan? Start { get; init; }

    public TimeSpan? Finish { get; init; }
}

public enum SingletonMode
{
    // A new run is skipped while another run with the same key is active.
    Skip,

    // A new run cancels the active run with the same key.
    Cancel,
}

// Ensures only one run per key is active at a time.
public sealed record Singleton(SingletonMode Mode)
{
    public string? Key { get; init; }
}
