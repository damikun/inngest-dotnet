// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

namespace Inngest.Client;

// Saves the runs of durable endpoint requests, which start in the app rather than in Inngest: the first save creates the
// run with the request as its event, later ones add steps to it.
internal interface IEndpointCheckpointApi
{
    // POST {api}/v1/checkpoint. Throws InngestException when Inngest did not create the run.
    Task<EndpointRun> CheckpointNewRunAsync(NewEndpointRun run, CancellationToken cancellationToken);

    // POST {api}/v1/checkpoint/{runId}/steps. Throws InngestException when Inngest did not save the steps.
    Task CheckpointEndpointStepsAsync(EndpointRun run, ReadOnlyMemory<byte> steps, CancellationToken cancellationToken);
}

// A run to create: the id the SDK gave it, the inngest/http.request event as JSON, the ops the request produced so far
// as a JSON array, and how often Inngest retries a failed step.
internal readonly record struct NewEndpointRun(string RunId, ReadOnlyMemory<byte> Event, ReadOnlyMemory<byte> Steps, int Retries);

// A run Inngest created. Later saves name its function and app; the token lets a client wait for the run's output.
internal sealed record EndpointRun(string RunId, string FunctionId, string AppId, string? Token);
