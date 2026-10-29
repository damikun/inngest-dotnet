// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using Inngest.Functions;

namespace Inngest.Execution;

// One POST from Inngest (or one Connect executor request) for a specific function.
internal sealed record ExecutionRequest
{
    public required FunctionDefinition Function { get; init; }

    // The raw JSON body: event, events, steps, ctx and version. The engine parses it.
    public required ReadOnlyMemory<byte> Body { get; init; }

    // The stepId query parameter: "step" for a normal call, or the hashed id of a planned step to run.
    public string? StepId { get; init; }

    // The scope that functions and middleware for this invocation are resolved from.
    public required IServiceProvider Services { get; init; }

    // Identifies this one call (X-Request-Id, or the Connect request id). Null when Inngest sent none.
    public string? RequestId { get; init; }

    // The queue job the call belongs to (X-Inngest-Job-Id). Null when Inngest sent none.
    public string? JobId { get; init; }

    // Which dispatch of the run this call is (X-Inngest-Generation-Id). Null when Inngest sent none.
    public int? GenerationId { get; init; }

    // How long the call may run steps under checkpointing when the options leave MaxRuntime unset. Connect workers
    // allow longer than HTTP calls, which a host or proxy may time out.
    public TimeSpan DefaultCheckpointMaxRuntime { get; init; } = Checkpointer.ServeMaxRuntime;
}
