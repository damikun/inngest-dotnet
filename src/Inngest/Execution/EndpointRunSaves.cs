// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Inngest.Execution;

// Records the runs of durable endpoint requests that finished, or failed for good, once the endpoint has answered. The
// saves run in the background, so that neither the client nor the next request on its connection waits for Inngest,
// and under a token of their own: the host cancels ApplicationStopping before the server lets its last requests
// finish, so a run that finished in that time would never be recorded. When the host stops, the saves still running
// are waited for, as the Go SDK waits for its checkpoints, for as long as the shutdown timeout allows. They are waited
// for once more after every other service has stopped, the server with its requests included, and whatever is left
// then is cancelled and logged.
//
// A save starts after the response, so a host that freezes or ends the process once a response is out, as serverless
// hosts do, can delay it or lose it. Each save holds the ops of its run until Inngest has them. While Inngest is slow
// or cannot be reached, past MaxRunning saves the request waits for its own after its response, so that saves cannot
// pile up without end.
internal sealed class EndpointRunSaves(ILogger<EndpointRunSaves> logger) : IHostedLifecycleService
{
    private readonly CancellationTokenSource _cancelled = new();
    private readonly Dictionary<Task, (string RunId, string FunctionId)> _running = [];
    private bool _stopped;

    // Cancelled once the app has stopped, or its shutdown timeout ran out. It also ends a hand-over that goes on after
    // its client left, which the server waits for as a request.
    public CancellationToken Stopped => _cancelled.Token;

    // How many saves may be under way before a request waits for its own.
    public int MaxRunning { get; init; } = 100;

    // Starts recording a run. The save logs its own failure and never throws. The task returned is the save when more
    // than MaxRunning are under way, which the request then waits for, and a completed one otherwise.
    public Task Start(string runId, string functionId, Func<CancellationToken, Task> save)
    {
        Task? task = null;
        var behind = false;
        lock (_running)
        {
            if (!_stopped)
            {
                var token = _cancelled.Token;
                task = Task.Run(() => RunAsync(save, token), CancellationToken.None);
                _running.Add(task, (runId, functionId));
                behind = _running.Count > MaxRunning;
            }
        }

        if (task is null)
        {
            Log.EndpointRunNotRecordedStopping(logger, runId, functionId);
            return Task.CompletedTask;
        }

        _ = task.ContinueWith(
            static (done, state) => ((EndpointRunSaves)state!).Forget(done),
            this,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        return behind ? task : Task.CompletedTask;
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StartingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => WaitAsync(cancellationToken);

    // A request the server let finish after this service stopped can still have started a save.
    public async Task StoppedAsync(CancellationToken cancellationToken)
    {
        await WaitAsync(cancellationToken).ConfigureAwait(false);
        Cancel();
    }

    private static async Task RunAsync(Func<CancellationToken, Task> save, CancellationToken cancellationToken)
    {
        try
        {
            await save(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            // Logged when it was cancelled.
        }
    }

    private async Task WaitAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (Running() is { Length: > 0 } running)
            {
                await Task.WhenAll(running).WaitAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Cancel();
        }
    }

    private Task[] Running()
    {
        lock (_running)
        {
            return [.. _running.Keys.Where(static save => !save.IsCompleted)];
        }
    }

    private void Forget(Task save)
    {
        lock (_running)
        {
            _running.Remove(save);
        }
    }

    private void Cancel()
    {
        (string RunId, string FunctionId)[] dropped;
        lock (_running)
        {
            if (_stopped)
            {
                return;
            }

            _stopped = true;
            dropped = [.. _running.Values];
            _running.Clear();
        }

        foreach (var (runId, functionId) in dropped)
        {
            Log.EndpointRunNotRecordedStopping(logger, runId, functionId);
        }

        _cancelled.Cancel();
    }
}
