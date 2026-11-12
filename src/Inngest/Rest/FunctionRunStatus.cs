// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

namespace Inngest;

public enum FunctionRunStatus
{
    /// <summary>A status this SDK does not know yet.</summary>
    Unknown,

    /// <summary>Waiting to start: queued. The dev server reports it in the runs of an event.</summary>
    Scheduled,

    Running,
    Completed,
    Failed,
    Cancelled,

    /// <summary>Not run, because flow control such as a rate limit skipped it. Only the v2 run lists report it.</summary>
    Skipped,

    /// <summary>Running, but its function is paused. Only the v2 REST API reports it.</summary>
    Paused,
}
