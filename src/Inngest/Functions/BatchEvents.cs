// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

namespace Inngest;

/// <summary>
/// Runs the function with up to MaxSize events at once, or with whatever arrived once Timeout passes.
/// </summary>
public sealed record BatchEvents(int MaxSize, TimeSpan Timeout)
{
    public string? Key { get; init; }

    public string? If { get; init; }
}
