// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using System.Collections.Concurrent;
using System.Text.Json;
using Inngest.Functions;
using Inngest.Internal;

namespace Inngest.Testing;

// Plays Inngest's executor for one run. Every call replays the function with the results so far. A lone new step
// runs inline; once an answer reports several steps, each runs in a targeted call of its own with immediate
// execution disabled, and the function is called again when none is pending, or after each one of a race. A retry
// repeats the call that failed with the next attempt number. Results are handed back in the order they completed.
// Sleeps, waits and invokes complete from mocks at the simulated time they would complete at, and the run waits on
// those without one.
internal sealed partial class RunDriver
{
    // What Inngest gives a function that sets no retries.
    private const int DefaultRetries = 4;

    // Far more than a real run needs; it only stops a function that never stops adding steps.
    private const int MaxCalls = 10_000;

    private static readonly AsyncLocal<RunDriver?> Calling = new();
    private static readonly byte[] Null = "null"u8.ToArray();

    private readonly InngestTestHost _host;
    private readonly FunctionDefinition _function;
    private readonly IReadOnlyList<byte[]> _events;
    private readonly StepMocks _mocks;
    private readonly TestCheckpoint? _stopAt;
    private readonly RunClock _clock;
    private readonly int _maxAttempts;

    // Results by hashed id, sorted the way Inngest sends them, and the order they completed in.
    private readonly SortedDictionary<string, byte[]> _memo = new(StringComparer.Ordinal);
    private readonly List<string> _stack = [];

    private readonly Dictionary<string, StepRecord> _steps = new(StringComparer.Ordinal);
    private readonly List<StepRecord> _found = [];

    // What the last call reported: the steps whose handler ran, and the steps it found without running them.
    private readonly List<StepRecord> _ranLast = [];
    private readonly List<StepRecord> _foundLast = [];
    private List<StepRecord>? _stoppedAt;

    // Steps Inngest has taken on but not finished: planned, sleeping, waiting, invoking or not understood.
    private readonly HashSet<string> _pending = new(StringComparer.Ordinal);

    private readonly PriorityQueue<Work, (DateTimeOffset At, long Sequence)> _queue = new();
    private readonly ConcurrentQueue<InngestEvent> _sent = new();
    private readonly ConcurrentQueue<InngestSignal<JsonElement>> _signals = new();
    private readonly ConcurrentQueue<PublishedMessage> _published = new();

    // What the host's middleware saw during the call in progress.
    private readonly Dictionary<string, Exception> _stepErrors = new(StringComparer.Ordinal);
    private readonly HashSet<string> _mocked = new(StringComparer.Ordinal);
    private Exception? _runError;

    private long _sequence;
    private int _calls;
    private bool _forceStepPlan;
    private bool _discoveryQueued;

    public RunDriver(InngestTestHost host, FunctionDefinition function, IReadOnlyList<byte[]> events, StepMocks mocks, TestCheckpoint? stopAt)
    {
        _host = host;
        _events = events;
        _mocks = mocks;
        _stopAt = stopAt;
        _clock = new RunClock(host.Time);
        _maxAttempts = (function.Options.Retries ?? DefaultRetries) + 1;
        RunId = Ulid.New(_clock.Now);

        // The engine only logs what it meets while resolving the function, such as a missing service, so the
        // exception is kept on the way.
        _function = new FunctionDefinition(function.Options, services =>
        {
            try
            {
                return function.Resolve(services);
            }
            catch (Exception exception)
            {
                _runError = exception;
                throw;
            }
        })
        {
            FailureHandlerFor = function.FailureHandlerFor,
        };
    }

    private enum WorkKind
    {
        Discover,
        Execute,
        Complete,
    }

    // The run whose call is in progress in this async flow.
    public static RunDriver? Current => Calling.Value;

    public string RunId { get; }

    public async Task<TestRun> RunAsync(CancellationToken cancellationToken)
    {
        QueueDiscovery(attempt: 0);
        while (_queue.TryDequeue(out var work, out var due))
        {
            cancellationToken.ThrowIfCancellationRequested();
            _clock.AdvanceTo(due.At);
            _ranLast.Clear();
            _foundLast.Clear();
            var finished = work.Kind switch
            {
                WorkKind.Discover => await DiscoverAsync(work, cancellationToken).ConfigureAwait(false),
                WorkKind.Execute => await ExecuteAsync(work, cancellationToken).ConfigureAwait(false),
                _ => Complete(work),
            };

            if (finished is not null)
            {
                return finished;
            }

            if (ReachedStop())
            {
                return Finish(TestRunStatus.Stopped);
            }
        }

        return Finish(TestRunStatus.Waiting);
    }

    // Called by the host's middleware during a call of this run.
    // The steps whose handler runs in the SDK and calls Inngest, such as a send-signal or a send-event step, are
    // answered by a mock of the same kind in the handler's place.
    public RunMock? FindRunMock(StepInfo step)
    {
        if (_mocks.Find(step.Id) is not RunMock mock || !mock.CanAnswer(step.Type))
        {
            return null;
        }

        _mocked.Add(step.HashedId);
        return mock;
    }

    public void CaptureStepError(string hashedId, Exception exception) => _stepErrors[hashedId] = exception;

    public void CaptureRunError(Exception exception) => _runError = exception;

    public void Sent(InngestEvent @event) => _sent.Enqueue(@event);

    public void Signaled(InngestSignal<JsonElement> signal) => _signals.Enqueue(signal);

    public void Published(PublishedMessage message) => _published.Enqueue(message);

    private async Task<TestRun?> DiscoverAsync(Work work, CancellationToken cancellationToken)
    {
        _discoveryQueued = false;
        var answer = await CallAsync(stepId: null, work.Attempt, work.JobId, cancellationToken).ConfigureAwait(false);
        return Handle(answer, work);
    }

    private async Task<TestRun?> ExecuteAsync(Work work, CancellationToken cancellationToken)
    {
        var step = work.Step!;
        var answer = await CallAsync(step.HashedId, work.Attempt, work.JobId, cancellationToken).ConfigureAwait(false);
        if (answer.IsStepNotFound())
        {
            // Inngest retries this, but replaying the same results in process reaches the same steps again.
            return Fail(new InvalidOperationException(
                $"Step \"{step.Id}\" was planned, but the function did not reach it when it was called to run it. " +
                "A function has to call its steps in the same way on every replay."));
        }

        return Handle(answer, work);
    }

    private TestRun? Handle(Answer answer, Work origin) => answer.StatusCode switch
    {
        200 => Finish(TestRunStatus.Completed, output: Parse(answer.Body)),
        206 => Report(answer.Body, origin),
        _ => Reject(answer, origin),
    };

    // The function failed: Inngest repeats the call until the attempts run out.
    private TestRun? Reject(Answer answer, Work origin)
    {
        if (answer.NoRetry == false && origin.Attempt + 1 < _maxAttempts)
        {
            Retry(origin);
            return null;
        }

        return Fail(_runError ?? Rebuild(answer.Body), answer.Body);
    }

    private void Retry(Work work)
    {
        if (work.Kind == WorkKind.Discover)
        {
            QueueDiscovery(work.Attempt + 1, work.JobId);
        }
        else
        {
            Enqueue(work with { Attempt = work.Attempt + 1 }, _clock.Now);
        }
    }

    // A retry keeps the job id of the call it repeats, as Inngest's queue item does.
    private void QueueDiscovery(int attempt, string? jobId = null)
    {
        if (!_discoveryQueued)
        {
            _discoveryQueued = true;
            Enqueue(new Work(WorkKind.Discover, attempt, JobId: jobId ?? Ulid.New(_clock.Now)), _clock.Now);
        }
    }

    private void Enqueue(Work work, DateTimeOffset at) => _queue.Enqueue(work, (at, _sequence++));

    private bool ReachedStop()
    {
        if (_stopAt is null)
        {
            return false;
        }

        switch (_stopAt.Kind)
        {
            case TestCheckpoint.CheckpointKind.StepsFound:
                if (_foundLast.Count > 0 && _stopAt.StepIds.All(id => _foundLast.Exists(step => step.Id == id)))
                {
                    _stoppedAt = [.. _foundLast];
                }

                break;

            case TestCheckpoint.CheckpointKind.StepRan:
                if (_ranLast.Find(step => step.Id == _stopAt.StepIds[0]) is { } ran)
                {
                    _stoppedAt = [ran];
                }

                break;

            default:
                // A step Inngest carries out is done once it is reported; a run step once it has run for good.
                if (_found.Find(step => step.Id == _stopAt.StepIds[0]) is { } step && (!step.Runnable || step.Status != TestStepStatus.Pending))
                {
                    _stoppedAt = [step];
                }

                break;
        }

        return _stoppedAt is not null;
    }

    // One item of the run's queue: a call that finds the next steps, a targeted call that runs a planned step, or
    // the result a mock gives a sleep, wait or invoke. A call has the job id Inngest sends as X-Inngest-Job-Id.
    private readonly record struct Work(
        WorkKind Kind,
        int Attempt = 0,
        StepRecord? Step = null,
        byte[]? Memo = null,
        Exception? Error = null,
        string? JobId = null);

    private sealed record Answer(int StatusCode, byte[] Body, bool? NoRetry)
    {
        // How the engine answers a targeted call whose step the function did not reach.
        public bool IsStepNotFound()
        {
            if (StatusCode != 500)
            {
                return false;
            }

            using var document = JsonDocument.Parse(Body);
            return document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("requestedStep", out _);
        }
    }

    private sealed class StepRecord(string hashedId, string id)
    {
        public string HashedId { get; } = hashedId;

        public string Id { get; } = id;

        public string Op { get; set; } = string.Empty;

        public StepType? Type { get; set; }

        public bool Runnable { get; set; }

        public bool Races { get; set; }

        public JsonElement Options { get; set; }

        public TestStepStatus Status { get; set; }

        public int Attempts { get; set; }

        public bool Mocked { get; set; }

        public JsonElement? Output { get; set; }

        public Exception? Error { get; set; }

        public List<Exception> Errors { get; } = [];

        public TestStep ToTestStep(JsonSerializerOptions options) =>
            new(Id, HashedId, Type, Op, Options, Status, Attempts, Mocked, Output, Error, [.. Errors], options);
    }
}
