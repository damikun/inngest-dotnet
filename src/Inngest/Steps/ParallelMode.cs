// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

namespace Inngest;

/// <summary>When Inngest calls the function again while steps started together are still running.</summary>
public enum ParallelMode
{
    /// <summary>Once all of them have finished, which takes the fewest calls. The default.</summary>
    Wait,

    /// <summary>
    /// Each time one of them finishes, so that a race goes on with its first result and a branch of steps in
    /// sequence does not wait for the other branches. A step of a race is never run in the call that finds it.
    /// </summary>
    Race,
}
