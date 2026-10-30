// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

namespace Inngest;

/// <summary>
/// Id is the id the code passed, including any ":&lt;n&gt;" suffix for repeated ids; HashedId is what Inngest stores.
/// Name is the display name the code gave the step, or null.
/// </summary>
public sealed record StepInfo(string Id, string HashedId, StepType Type, bool Memoized)
{
    public string? Name { get; init; }
}
