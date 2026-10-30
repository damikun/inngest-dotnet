// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

namespace Inngest;

/// <summary>Delays a run until no new matching event arrives for Period, then runs once with the last event.</summary>
public sealed record Debounce(TimeSpan Period)
{
    public string? Key { get; init; }

    /// <summary>Upper bound on how long a run can be delayed by a stream of events.</summary>
    public TimeSpan? Timeout { get; init; }
}
