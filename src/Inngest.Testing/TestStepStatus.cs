// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

namespace Inngest.Testing;

public enum TestStepStatus
{
    /// <summary>
    /// Inngest has the step but no result: it is planned, or waits on something the test did not mock.
    /// </summary>
    Pending,

    /// <summary>The step has a result, which the function receives on every replay.</summary>
    Completed,

    /// <summary>The step failed for good, and the function receives a StepFailedException where it awaits it.</summary>
    Failed,
}
