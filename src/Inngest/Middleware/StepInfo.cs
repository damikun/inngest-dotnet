// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

namespace Inngest;

public enum StepType
{
    Run,
    Sleep,
    WaitForEvent,
    Invoke,
    SendEvent,
    WaitForSignal,
    SendSignal,
    Fetch,
    AIInfer,
}

// Id is the id the code passed, including any ":<n>" suffix for repeated ids; HashedId is what Inngest stores.
// Name is the display name the code gave the step, or null.
public sealed record StepInfo(string Id, string HashedId, StepType Type, bool Memoized)
{
    public string? Name { get; init; }
}
