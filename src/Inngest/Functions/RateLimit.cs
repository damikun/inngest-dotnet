// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

namespace Inngest;

/// <summary>Drops runs beyond Limit per Period.</summary>
public sealed record RateLimit(int Limit, TimeSpan Period)
{
    public string? Key { get; init; }
}
