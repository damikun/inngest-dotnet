using System.Text;
using System.Text.Json;
using Inngest.Client;

namespace Inngest.Tests.Checkpointing;

// Records the runs that durable endpoint requests create and the steps they add, and answers like Inngest does unless
// a test makes the next call fail.
internal sealed class FakeEndpointCheckpointApi : IEndpointCheckpointApi
{
    private readonly Lock _gate = new();
    private readonly List<EndpointSave> _saves = [];
    private readonly List<(int Count, TaskCompletionSource Reached)> _waiters = [];
    private Exception? _failNext;
    private (TaskCompletionSource Reached, TaskCompletionSource Release)? _holdNext;

    public string? Token { get; set; } = "eyJ.token";

    // Called once a call is saved, before the next one starts.
    public Action<EndpointSave>? Saved { get; set; }

    public IReadOnlyList<EndpointSave> Saves
    {
        get
        {
            lock (_gate)
            {
                return [.. _saves];
            }
        }
    }

    // Completes once this many calls have been saved, such as a run a request records after its response.
    public Task WaitForSavesAsync(int count)
    {
        lock (_gate)
        {
            if (_saves.Count >= count)
            {
                return Task.CompletedTask;
            }

            var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _waiters.Add((count, reached));
            return reached.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        }
    }

    public FakeEndpointCheckpointApi FailNext(Exception exception)
    {
        lock (_gate)
        {
            _failNext = exception;
        }

        return this;
    }

    // The next call waits until the test releases it, and is saved only then. Reached completes once it has started.
    public (Task Reached, TaskCompletionSource Release) HoldNext()
    {
        var hold = (new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously), new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
        lock (_gate)
        {
            _holdNext = hold;
        }

        return (hold.Item1.Task, hold.Item2);
    }

    public async Task<EndpointRun> CheckpointNewRunAsync(NewEndpointRun run, CancellationToken cancellationToken)
    {
        var save = new EndpointSave(Created: true, run.RunId, Encoding.UTF8.GetString(run.Event.Span), Text(run.Steps), run.Retries);
        await SaveAsync(save, cancellationToken);
        return new EndpointRun(run.RunId, "fn-uuid", "app-uuid", Token);
    }

    public Task CheckpointEndpointStepsAsync(EndpointRun run, ReadOnlyMemory<byte> steps, CancellationToken cancellationToken) =>
        SaveAsync(new EndpointSave(Created: false, run.RunId, Event: null, Text(steps), Retries: null), cancellationToken);

    private async Task SaveAsync(EndpointSave save, CancellationToken cancellationToken)
    {
        // As the HTTP client does with a token that is already cancelled.
        cancellationToken.ThrowIfCancellationRequested();
        (TaskCompletionSource Reached, TaskCompletionSource Release)? hold;
        lock (_gate)
        {
            if (TakeFailure() is { } failure)
            {
                throw failure;
            }

            hold = _holdNext;
            _holdNext = null;
        }

        if (hold is { } held)
        {
            held.Reached.TrySetResult();
            await held.Release.Task.WaitAsync(cancellationToken);
        }

        lock (_gate)
        {
            _saves.Add(save);
            Notify();
        }

        Saved?.Invoke(save);
    }

    private void Notify()
    {
        foreach (var waiter in _waiters.Where(waiter => waiter.Count <= _saves.Count).ToList())
        {
            waiter.Reached.TrySetResult();
            _waiters.Remove(waiter);
        }
    }

    private Exception? TakeFailure()
    {
        var failure = _failNext;
        _failNext = null;
        return failure;
    }

    private static string Text(ReadOnlyMemory<byte> steps) => steps.IsEmpty ? "[]" : Encoding.UTF8.GetString(steps.Span);
}

// One call: a run created with its event, or steps added to it.
internal sealed record EndpointSave(bool Created, string RunId, string? Event, string Steps, int? Retries)
{
    public IReadOnlyList<JsonElement> Ops
    {
        get
        {
            using var document = JsonDocument.Parse(Steps);
            return [.. document.RootElement.EnumerateArray().Select(op => op.Clone())];
        }
    }

    public IReadOnlyList<string> OpNames => [.. Ops.Select(op => op.GetProperty("op").GetString()!)];
}
