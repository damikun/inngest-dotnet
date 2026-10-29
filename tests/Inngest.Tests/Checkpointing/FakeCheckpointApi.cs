using System.Net;
using System.Text;
using System.Text.Json;
using Inngest.Client;

namespace Inngest.Tests.Checkpointing;

// Records every checkpoint and answers from a script: saved unless a test says otherwise.
internal sealed class FakeCheckpointApi : ICheckpointApi
{
    private readonly Lock _gate = new();
    private readonly Queue<Func<CancellationToken, Task>> _script = new();
    private readonly List<Checkpoint> _checkpoints = [];
    private readonly List<(int Count, TaskCompletionSource Reached)> _waiters = [];

    public IReadOnlyList<Checkpoint> Checkpoints
    {
        get
        {
            lock (_gate)
            {
                return [.. _checkpoints];
            }
        }
    }

    // When set, each checkpoint is also written here, to compare its timing with what the function did.
    public List<string>? Timeline { get; set; }

    // Completes once this many checkpoints have been asked for in total.
    public Task Saved(int count)
    {
        lock (_gate)
        {
            if (_checkpoints.Count >= count)
            {
                return Task.CompletedTask;
            }

            var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _waiters.Add((count, reached));
            return reached.Task;
        }
    }

    // The next checkpoint fails as the client reports a failure that retries did not fix.
    public FakeCheckpointApi Fail(int statusCode = 500) =>
        Then(_ => Task.FromException(new InngestException($"Inngest answered {statusCode} to the checkpoint.", statusCode)));

    // The next checkpoint is refused because Inngest dispatched the run again.
    public FakeCheckpointApi Stale() => Fail((int)HttpStatusCode.Conflict);

    // The next checkpoint fails with something other than an InngestException.
    public FakeCheckpointApi Throw(Exception exception) => Then(_ => Task.FromException(exception));

    // The next checkpoint waits until the test completes the returned source.
    public TaskCompletionSource Hold()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Then(cancellationToken => release.Task.WaitAsync(cancellationToken));
        return release;
    }

    public Task CheckpointAsync(AsyncCheckpoint checkpoint, CancellationToken cancellationToken)
    {
        var recorded = new Checkpoint(
            checkpoint.RunId,
            checkpoint.FunctionId,
            checkpoint.QueueItemId,
            checkpoint.RequestId,
            checkpoint.GenerationId,
            checkpoint.RequestStartedAt,
            Encoding.UTF8.GetString(checkpoint.Steps.Span));

        Func<CancellationToken, Task>? answer;
        lock (_gate)
        {
            _checkpoints.Add(recorded);
            Timeline?.Add($"checkpoint {string.Join(",", recorded.DisplayNames)}");
            _script.TryDequeue(out answer);
            foreach (var waiter in _waiters.Where(waiter => waiter.Count <= _checkpoints.Count))
            {
                waiter.Reached.TrySetResult();
            }
        }

        return answer is null ? Task.CompletedTask : answer(cancellationToken);
    }

    private FakeCheckpointApi Then(Func<CancellationToken, Task> answer)
    {
        lock (_gate)
        {
            _script.Enqueue(answer);
        }

        return this;
    }
}

internal sealed record Checkpoint(
    string RunId,
    string FunctionId,
    string QueueItemId,
    string? RequestId,
    int? GenerationId,
    DateTimeOffset RequestStartedAt,
    string Steps)
{
    public JsonElement[] Ops => [.. JsonDocument.Parse(Steps).RootElement.EnumerateArray()];

    public string[] DisplayNames => [.. Ops.Select(op => op.GetProperty("displayName").GetString()!)];
}
