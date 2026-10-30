// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using Inngest.Client;

namespace Inngest.Execution;

// A request to a durable endpoint that starts a run. Its steps run in the call and wait in a buffer: Inngest creates
// the run when a step has to wait or fails, which hands the rest of the run to Inngest, or after the response once the
// endpoint has finished. Event is the inngest/http.request event the run is created with.
internal sealed record EndpointCall(ReadOnlyMemory<byte> Event, int Retries);

internal enum EndpointOutcomeKind
{
    // The endpoint finished in this request. Output is its response as {"status", "headers", "body"}.
    Completed,

    // A step has to wait or failed, and Inngest took the run over. The caller waits for the output with Token.
    HandedOver,

    // The endpoint failed for good, or the run could not be handed over. Error is the reason.
    Failed,
}

// What became of a durable endpoint request.
internal sealed class EndpointOutcome
{
    private EndpointOutcome(EndpointOutcomeKind kind, string runId)
    {
        Kind = kind;
        RunId = runId;
    }

    public EndpointOutcomeKind Kind { get; }

    public string RunId { get; }

    public object? Output { get; private init; }

    public string? Token { get; private init; }

    public Exception? Error { get; private init; }

    // Creates the run with the steps of a request that finished or failed, once the response is on its way. Null when
    // the request ran no steps, which leaves no run to record.
    public Func<CancellationToken, Task>? Finish { get; private init; }

    public static EndpointOutcome Completed(string runId, object? output, Func<CancellationToken, Task>? finish) =>
        new(EndpointOutcomeKind.Completed, runId) { Output = output, Finish = finish };

    public static EndpointOutcome HandedOver(string runId, string token) =>
        new(EndpointOutcomeKind.HandedOver, runId) { Token = token };

    public static EndpointOutcome Failed(string runId, Exception error, Func<CancellationToken, Task>? finish = null) =>
        new(EndpointOutcomeKind.Failed, runId) { Error = error, Finish = finish };
}

// Saves the steps of a durable endpoint request: the first save creates the run, later ones add to it.
internal sealed class EndpointSaver(IEndpointCheckpointApi api, string runId, EndpointCall call)
{
    public string RunId => runId;

    // Set once Inngest has created the run.
    public EndpointRun? Run { get; private set; }

    public async Task SaveAsync(ReadOnlyMemory<byte> steps, CancellationToken cancellationToken)
    {
        if (Run is { } run)
        {
            await api.CheckpointEndpointStepsAsync(run, steps, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            Run = await api.CheckpointNewRunAsync(new NewEndpointRun(runId, call.Event, steps, call.Retries), cancellationToken).ConfigureAwait(false);
        }
    }
}
