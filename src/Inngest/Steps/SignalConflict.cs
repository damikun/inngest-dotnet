// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

namespace Inngest;

public enum SignalConflict
{
    /// <summary>The run fails when another run already waits for the signal.</summary>
    Fail,

    /// <summary>The run takes the signal over, and the other run's wait stays pending until its timeout.</summary>
    Replace,
}
