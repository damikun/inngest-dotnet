// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

namespace Inngest;

/// <summary>What starts a function run: a matching event or a cron schedule.</summary>
public abstract record FunctionTrigger
{
    private protected FunctionTrigger()
    {
    }
}
