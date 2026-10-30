// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

namespace Inngest;

/// <summary>
/// Options for a single send. Environment sends to another branch environment than the app's, for this call only.
/// </summary>
public sealed record SendOptions
{
    public string? Environment { get; init; }
}
