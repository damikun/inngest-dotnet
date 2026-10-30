// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

namespace Inngest;

/// <summary>Queues runs beyond Limit per Period instead of dropping them.</summary>
public sealed record Throttle(int Limit, TimeSpan Period)
{
    public int? Burst { get; init; }

    public string? Key { get; init; }
}
