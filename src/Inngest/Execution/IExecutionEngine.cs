// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

namespace Inngest.Execution;

internal interface IExecutionEngine
{
    // Runs one invocation of a function: replays memoized steps, then executes or reports new ones.
    Task<ExecutionResult> ExecuteAsync(ExecutionRequest request, CancellationToken cancellationToken);
}
