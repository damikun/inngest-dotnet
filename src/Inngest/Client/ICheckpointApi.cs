// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

namespace Inngest.Client;

// Saves the steps a call has run to the run's state, so that the call can go on with the function instead of
// answering Inngest after every step.
internal interface ICheckpointApi
{
    // Throws InngestException when the steps could not be saved. StatusCode 409 means Inngest has dispatched the run
    // again since this call started, so the call must not run any more steps.
    Task CheckpointAsync(AsyncCheckpoint checkpoint, CancellationToken cancellationToken);
}

// The steps one call ran, and which call and queue item ran them. Steps is a JSON array of StepRun ops; FunctionId
// and QueueItemId are the ctx.fn_id and ctx.qi_id of the call.
internal readonly record struct AsyncCheckpoint(
    string RunId,
    string FunctionId,
    string QueueItemId,
    string? RequestId,
    int? GenerationId,
    DateTimeOffset RequestStartedAt,
    ReadOnlyMemory<byte> Steps);
