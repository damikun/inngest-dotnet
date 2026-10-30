// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

namespace Inngest;

/// <summary>
/// Signal is the name the run waits under, such as "approval/order-123". Only one run in an environment can wait for
/// a signal at a time; OnConflict decides what happens when another run already does.
/// </summary>
public sealed record WaitForSignalOptions(string Signal, TimeSpan Timeout)
{
    public SignalConflict OnConflict { get; init; }
}
