// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

namespace Inngest;

/// <summary>
/// Runs the function for every event with this name, optionally filtered by an expression such as
/// "event.data.total &gt; 100".
/// </summary>
public sealed record EventTrigger(string Event) : FunctionTrigger
{
    public string? If { get; init; }
}
