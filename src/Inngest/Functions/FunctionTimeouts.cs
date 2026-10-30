// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

namespace Inngest;

/// <summary>
/// Start limits the time a run may wait in the queue before its first step; Finish limits the time from the
/// first step to completion. Runs that exceed either are cancelled.
/// </summary>
public sealed record FunctionTimeouts
{
    public TimeSpan? Start { get; init; }

    public TimeSpan? Finish { get; init; }
}
