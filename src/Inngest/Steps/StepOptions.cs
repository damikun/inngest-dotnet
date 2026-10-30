// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

namespace Inngest;

/// <summary>
/// Id is what Inngest memoizes the step by, so it has to stay the same; Name is only what the Inngest UI shows and
/// can change at any time. ParallelMode applies to this step instead of the mode of an enclosing ParallelAsync.
/// </summary>
public sealed record StepOptions(string Id)
{
    public string? Name { get; init; }

    public ParallelMode? ParallelMode { get; init; }

    public static implicit operator StepOptions(string id) => new(id);
}
