// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

namespace Inngest.Testing;

public enum TestRunStatus
{
    /// <summary>The function returned; Output holds what it returned.</summary>
    Completed,

    /// <summary>The function failed for good; Error holds what it failed with.</summary>
    Failed,

    /// <summary>
    /// The run waits on steps the test did not mock, or that the test host does not support; see WaitingOn.
    /// </summary>
    Waiting,

    /// <summary>ExecuteStepAsync reached its step and stopped the run there.</summary>
    Stopped,
}
