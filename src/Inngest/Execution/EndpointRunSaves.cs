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
// then is cancelled. A save that ends by being cancelled is logged: its call may or may not have reached Inngest.
//
// A save starts after the response, so a host that freezes or ends the process once a response is out, as serverless
// hosts do, can delay it or lose it. Each save holds the ops of its run until Inngest has them. While Inngest is slow
// or cannot be reached, past MaxRunning saves the request waits for its own after its response, so that saves cannot
// pile up without end.
internal sealed class EndpointRunSaves : IHostedLifecycleService, IDisposable
{
    // How long the saves that were cancelled get to end, so that they are logged before the app says it has stopped.
    private static readonly TimeSpan CancelledGrace = TimeSpan.FromSeconds(1);

    private readonly ILogger<EndpointRunSaves> _logger;
    private readonly CancellationTokenSource _cancelled = new();
    private readonly HashSet<Task> _running = [];
    private bool _stopped;

    public EndpointRunSaves(ILogger<EndpointRunSaves> logger)
    {
        _logger = logger;

        // Taken here, as the source's Token cannot be read once it is disposed, and hand-overs read this after that.
        Stopped = _cancelled.Token;
    }

    // Cancelled once the app has stopped, or its shutdown timeout ran out. It also ends a hand-over that goes on after
    // its client left, which the server waits for as a request.
    public CancellationToken Stopped { get; }

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
                task = Task.Run(() => RunAsync(runId, functionId, save), CancellationToken.None);
                _running.Add(task);
                behind = _running.Count > MaxRunning;
            }
        }

        if (task is null)
        {
            Log.EndpointRunNotRecordedStopping(_logger, runId, functionId);
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
        await CancelAsync().ConfigureAwait(false);
    }

    // The container disposes the service after the host has stopped it, or instead of that.
    public void Dispose()
    {
        if (Stop() is not null)
        {
            _cancelled.Cancel();
        }

        _cancelled.Dispose();
    }

    private async Task RunAsync(string runId, string functionId, Func<CancellationToken, Task> save)
    {
        try
        {
            await save(Stopped).ConfigureAwait(false);
        }
        catch (Exception) when (Stopped.IsCancellationRequested)
        {
            Log.EndpointRunSaveCancelled(_logger, runId, functionId);
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
            await CancelAsync().ConfigureAwait(false);
        }
    }

    private Task[] Running()
    {
        lock (_running)
        {
            return [.. _running.Where(static save => !save.IsCompleted)];
        }
    }

    private void Forget(Task save)
    {
        lock (_running)
        {
            _running.Remove(save);
        }
    }

    // The saves still running, or null once stopped. Saves started from now on are not run.
    private Task[]? Stop()
    {
        lock (_running)
        {
            if (_stopped)
            {
                return null;
            }

            _stopped = true;
            return [.. _running];
        }
    }

    private async Task CancelAsync()
    {
        if (Stop() is not { } running)
        {
            return;
        }

        _cancelled.Cancel();
        try
        {
            await Task.WhenAll(running).WaitAsync(CancelledGrace).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // A save that does not end when it is cancelled is left to itself.
        }
    }
}
