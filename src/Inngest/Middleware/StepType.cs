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
    AIWrap,
    RealtimePublish,
}
