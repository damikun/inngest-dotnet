// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

namespace Inngest;

/// <summary>Ensures only one run per key is active at a time.</summary>
public sealed record Singleton(SingletonMode Mode)
{
    public string? Key { get; init; }
}
