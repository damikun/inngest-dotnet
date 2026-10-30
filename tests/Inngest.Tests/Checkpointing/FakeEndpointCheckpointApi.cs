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

    public string? Token { get; set; } = "eyJ.token";

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

    public Task<EndpointRun> CheckpointNewRunAsync(NewEndpointRun run, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (TakeFailure() is { } failure)
            {
                return Task.FromException<EndpointRun>(failure);
            }

            _saves.Add(new EndpointSave(Created: true, run.RunId, Encoding.UTF8.GetString(run.Event.Span), Text(run.Steps), run.Retries));
            Notify();
        }

        return Task.FromResult(new EndpointRun(run.RunId, "fn-uuid", "app-uuid", Token));
    }

    public Task CheckpointEndpointStepsAsync(EndpointRun run, ReadOnlyMemory<byte> steps, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (TakeFailure() is { } failure)
            {
                return Task.FromException(failure);
            }

            _saves.Add(new EndpointSave(Created: false, run.RunId, Event: null, Text(steps), Retries: null));
            Notify();
        }

        return Task.CompletedTask;
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
