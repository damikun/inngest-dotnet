// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using System.Collections.Concurrent;
using Inngest.Client;

namespace Inngest.Execution;

// A request to a durable endpoint that starts a run. Its steps run in the call and wait in a buffer: Inngest creates
// the run when a step has to wait or fails, which hands the rest of the run to Inngest, or after the response once the
// endpoint has finished. Event is the inngest/http.request event the run is created with. Once Inngest has created the
// run, the rest of a hand-over runs under Stopped rather than the request's token: it ends when the app stops, and
// never by default.
internal sealed record EndpointCall(ReadOnlyMemory<byte> Event, int Retries, CancellationToken Stopped = default);

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

// Saves the steps of a durable endpoint request. The first call creates the run with the request's event and the first
// op, later ones add to the run.
internal sealed class EndpointSaver(IEndpointCheckpointApi api, EndpointFirstRuns firstRuns, string functionId, string runId, EndpointCall call)
{
    // Room for ops in one call: Inngest reads 4 MiB of the call that creates a run, of which the rest of the body takes a
    // few hundred bytes. Calls that add steps keep under it too.
    private const int MaxOpsLength = CheckpointApiClient.MaxNewRunLength - 1024;

    public string RunId => runId;

    // Set once Inngest has created the run.
    public EndpointRun? Run { get; private set; }

    // A run Inngest takes over. Inngest takes more than one op in a call, other than lazy ones, for steps in parallel and
    // then plans every later step of the run before it runs, which also starts the attempts of a failed step over. So,
    // as in inngest-js, the steps that ran go one to a call, and the last ops, the steps in parallel when there are
    // several, go together. The request's token cancels only the call that creates the run: a run that Inngest has
    // without the ops that hand it over has nothing queued, so the rest goes on when the client leaves.
    public async Task HandOverAsync(EndpointOps ops, int last, CancellationToken cancellationToken)
    {
        var ran = ops.Count - last;
        for (var i = 0; i < ran; i++)
        {
            await SaveAsync(ops, i, 1, Run is null ? cancellationToken : call.Stopped).ConfigureAwait(false);
        }

        if (last > 0)
        {
            await SaveAsync(ops, ran, last, Run is null ? cancellationToken : call.Stopped).ConfigureAwait(false);
        }
    }

    // A run that ended in the request, with RunComplete as its last op. Inngest never calls it again, so its ops share
    // calls, one for most requests, and only the size of a call splits them.
    public async Task FinishAsync(EndpointOps ops, CancellationToken cancellationToken)
    {
        var room = MaxOpsLength - (Run is null ? call.Event.Length : 0);
        for (var from = 0; from < ops.Count;)
        {
            var count = 1;
            while (from + count < ops.Count && ops.Length(from, count + 1) <= room)
            {
                count++;
            }

            await SaveAsync(ops, from, count, cancellationToken).ConfigureAwait(false);
            from += count;
            room = MaxOpsLength;
        }
    }

    private async Task SaveAsync(EndpointOps ops, int from, int count, CancellationToken cancellationToken)
    {
        var steps = ops.Slice(from, count);
        if (Run is { } run)
        {
            await firstRuns.StoredAsync(functionId, cancellationToken).ConfigureAwait(false);
            await api.CheckpointEndpointStepsAsync(run, steps, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            Run = await api.CheckpointNewRunAsync(new NewEndpointRun(runId, call.Event, steps, call.Retries), cancellationToken).ConfigureAwait(false);
            firstRuns.Created(functionId);
        }
    }
}

// When this app first created a run of each durable endpoint. Inngest stores an endpoint's function in the background
// of the call that creates its first run, and until it has, it drops the steps added to the endpoint's runs while
// still answering 200: a sleep that hands a run over is lost, and the run never ends. So a call that adds steps waits
// until a second has passed since the app first created a run of the endpoint, which only its first requests ever do.
internal sealed class EndpointFirstRuns(TimeProvider time)
{
    private readonly ConcurrentDictionary<string, long> _created = new(StringComparer.Ordinal);

    public TimeSpan Wait { get; init; } = TimeSpan.FromSeconds(1);

    public void Created(string functionId) => _created.TryAdd(functionId, time.GetTimestamp());

    public Task StoredAsync(string functionId, CancellationToken cancellationToken)
    {
        var left = _created.TryGetValue(functionId, out var created) ? Wait - time.GetElapsedTime(created) : TimeSpan.Zero;
        return left > TimeSpan.Zero ? Task.Delay(left, time, cancellationToken) : Task.CompletedTask;
    }
}

// The ops a durable endpoint request buffered, as one JSON array, and where each op ends in it.
internal sealed class EndpointOps(byte[] array, int[] ends)
{
    public static readonly EndpointOps Empty = new([], []);

    public int Count => ends.Length;

    // The length of ops [from, from + count) as a JSON array.
    public int Length(int from, int count) => Span(from, count).Length + 2;

    // Ops [from, from + count) as a JSON array of their own.
    public ReadOnlyMemory<byte> Slice(int from, int count)
    {
        if (from == 0 && count == Count)
        {
            return array;
        }

        var ops = Span(from, count);
        var slice = new byte[ops.Length + 2];
        slice[0] = (byte)'[';
        ops.CopyTo(slice.AsSpan(1));
        slice[^1] = (byte)']';
        return slice;
    }

    // The ops with the commas between them. The first op follows the opening bracket, every other one a comma.
    private ReadOnlySpan<byte> Span(int from, int count)
    {
        var start = from == 0 ? 1 : ends[from - 1] + 1;
        return array.AsSpan(start, ends[from + count - 1] - start);
    }
}
