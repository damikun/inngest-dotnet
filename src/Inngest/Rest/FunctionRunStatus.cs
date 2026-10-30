// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

namespace Inngest;

public enum FunctionRunStatus
{
    /// <summary>A status this SDK does not know yet.</summary>
    Unknown,

    /// <summary>Waiting to start. The dev server reports it in the runs of an event.</summary>
    Scheduled,

    Running,
    Completed,
    Failed,
    Cancelled,
}
