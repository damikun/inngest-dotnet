// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using System.Buffers;
using System.Collections.Concurrent;
using System.Text.Json;
using Inngest.Errors;
using Inngest.Execution;
using Inngest.Functions;
using Inngest.Internal;

namespace Inngest.Testing;

// Plays Inngest's executor for one run. Every call replays the function with the results so far. A lone new step
// runs inline; once an answer reports several steps, each runs in a targeted call of its own with immediate
// execution disabled, and the function is called again when none is pending, or after each one of a race. A retry
// repeats the call that failed with the next attempt number. Results are handed back in the order they completed.
// Sleeps, waits and invokes complete from mocks at the simulated time they would complete at, and the run waits on
// those without one.
internal sealed class RunDriver
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
    private readonly string? _stopAt;
    private readonly RunClock _clock;
    private readonly int _maxAttempts;

    // Results by hashed id, sorted the way Inngest sends them, and the order they completed in.
    private readonly SortedDictionary<string, byte[]> _memo = new(StringComparer.Ordinal);
    private readonly List<string> _stack = [];

    private readonly Dictionary<string, StepRecord> _steps = new(StringComparer.Ordinal);
    private readonly List<StepRecord> _found = [];

    // Steps Inngest has taken on but not finished: planned, sleeping, waiting, invoking or not understood.
    private readonly HashSet<string> _pending = new(StringComparer.Ordinal);

    private readonly PriorityQueue<Work, (DateTimeOffset At, long Sequence)> _queue = new();
    private readonly ConcurrentQueue<InngestEvent> _sent = new();

    // What the host's middleware saw during the call in progress.
    private readonly Dictionary<string, Exception> _stepErrors = new(StringComparer.Ordinal);
    private readonly HashSet<string> _mocked = new(StringComparer.Ordinal);
    private Exception? _runError;

    private long _sequence;
    private int _calls;
    private bool _forceStepPlan;
    private bool _discoveryQueued;

    public RunDriver(InngestTestHost host, FunctionDefinition function, IReadOnlyList<byte[]> events, StepMocks mocks, string? stopAt)
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
    // A send-signal step is a run step whose handler calls Inngest, so a run mock stands in for it as well.
    public RunMock? FindRunMock(StepInfo step)
    {
        if (step.Type is not (StepType.Run or StepType.SendSignal) || _mocks.Find(step.Id) is not RunMock mock)
        {
            return null;
        }

        _mocked.Add(step.HashedId);
        return mock;
    }

    public void CaptureStepError(string hashedId, Exception exception) => _stepErrors[hashedId] = exception;

    public void CaptureRunError(Exception exception) => _runError = exception;

    public void Sent(InngestEvent @event) => _sent.Enqueue(@event);

    private async Task<TestRun?> DiscoverAsync(Work work, CancellationToken cancellationToken)
    {
        _discoveryQueued = false;
        var answer = await CallAsync(stepId: null, work.Attempt, cancellationToken).ConfigureAwait(false);
        return Handle(answer, work);
    }

    private async Task<TestRun?> ExecuteAsync(Work work, CancellationToken cancellationToken)
    {
        var step = work.Step!;
        var answer = await CallAsync(step.HashedId, work.Attempt, cancellationToken).ConfigureAwait(false);
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

    private TestRun? Report(byte[] body, Work origin)
    {
        using var document = JsonDocument.Parse(body);
        var ops = document.RootElement;

        // As in Inngest: once an answer holds several steps, every new step is planned until none is pending.
        if (ops.GetArrayLength() > 1)
        {
            _forceStepPlan = true;
        }

        foreach (var element in ops.EnumerateArray())
        {
            var op = ReportedOp.Read(element);
            switch (op.Code)
            {
                case ReportedOp.StepRun:
                    Ran(op);
                    break;
                case ReportedOp.StepError:
                    Errored(op, origin);
                    break;
                case ReportedOp.StepFailed:
                    Failed(op);
                    break;
                default:
                    Take(op);
                    break;
            }
        }

        return null;
    }

    private void Ran(ReportedOp op)
    {
        var step = Executed(op);
        step.Output = op.Data.ValueKind == JsonValueKind.Undefined ? Parse(Null) : op.Data.Clone();
        Settle(step, Wrap("data", op.Data), TestStepStatus.Completed);
    }

    private void Errored(ReportedOp op, Work origin)
    {
        var step = Executed(op);
        var error = TakeStepError(step, op.Error);
        step.Errors.Add(error);
        if (origin.Attempt + 1 < _maxAttempts)
        {
            Retry(origin);
            return;
        }

        // Inngest turns an error on the last attempt into a failure.
        step.Error = error;
        Settle(step, Wrap("error", op.Error), TestStepStatus.Failed);
    }

    private void Failed(ReportedOp op)
    {
        var step = Executed(op);
        step.Error = TakeStepError(step, op.Error);
        step.Errors.Add(step.Error);
        Settle(step, Wrap("error", op.Error), TestStepStatus.Failed);
    }

    private StepRecord Executed(ReportedOp op)
    {
        var step = Track(op);
        step.Attempts++;
        step.Mocked |= _mocked.Contains(step.HashedId);
        return step;
    }

    // A step Inngest carries out itself, or plans for a call of its own.
    private void Take(ReportedOp op)
    {
        // A race calls the function while other steps are still pending, and the function reports those again.
        if (_pending.Contains(op.HashedId))
        {
            return;
        }

        var step = Track(op);
        _pending.Add(step.HashedId);
        var mock = _mocks.Find(step.Id);
        switch (op.Code)
        {
            case ReportedOp.StepPlanned:
                Enqueue(new Work(WorkKind.Execute, Step: step), _clock.Now);
                break;

            case ReportedOp.Sleep when mock is not null || _mocks.EndsAllSleeps:
                step.Mocked = true;
                Enqueue(new Work(WorkKind.Complete, Step: step, Memo: Null), WakeTime(op));
                break;

            case ReportedOp.WaitForEvent when mock is WaitMock { Received: { } received }:
                step.Mocked = true;
                var expected = ReportedOp.Text(op.Options, "event");
                if (expected is not null && !string.Equals(expected, received.Name, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"Step \"{step.Id}\" waits for \"{expected}\", but its mock receives \"{received.Name}\".");
                }

                var @event = EventJson.Serialize(received, _host.SerializerOptions, _clock.Now);
                Enqueue(new Work(WorkKind.Complete, Step: step, Memo: @event), _clock.Now);
                break;

            case ReportedOp.WaitForEvent when mock is WaitMock:
                step.Mocked = true;
                Enqueue(new Work(WorkKind.Complete, Step: step, Memo: Null), _clock.Now + Timeout(op));
                break;

            case ReportedOp.InvokeFunction when mock is InvokeMock invoke:
                step.Mocked = true;
                Enqueue(new Work(WorkKind.Complete, Step: step, Memo: invoke.ToMemo(_host.SerializerOptions), Error: invoke.Error), _clock.Now);
                break;
        }
    }

    // A mock completes a sleep, wait or invoke.
    private TestRun? Complete(Work work)
    {
        var step = work.Step!;
        if (work.Error is { } error)
        {
            step.Error = error;
            step.Errors.Add(error);
            Settle(step, work.Memo!, TestStepStatus.Failed);
            return null;
        }

        var memo = Parse(work.Memo!);
        step.Output = step.Type == StepType.Invoke && memo.TryGetProperty("data", out var data) ? data : memo;
        Settle(step, work.Memo!, TestStepStatus.Completed);
        return null;
    }

    private StepRecord Track(ReportedOp op)
    {
        if (!_steps.TryGetValue(op.HashedId, out var step))
        {
            if (_mocks.Find(op.Id) is { } mock && mock.Answers != op.Type)
            {
                throw new InvalidOperationException(ReportedOp.IsSupported(op.Type)
                    ? $"The mock for step \"{op.Id}\" answers a {Describe(mock.Answers)}, but the step is a {Describe(op.Type!.Value)}."
                    : $"Step \"{op.Id}\" is a {op.Code} step, which the test host does not support, so it cannot be mocked.");
            }

            step = new StepRecord(op.HashedId, op.Id);
            _steps.Add(op.HashedId, step);
            _found.Add(step);
        }

        step.Op = op.Code;
        step.Type = op.Type;
        step.Runnable = op.Runnable;
        step.Races = op.Races;
        if (op.Options.ValueKind == JsonValueKind.Object)
        {
            step.Options = op.Options;
        }

        return step;
    }

    private static string Describe(StepType type) => type switch
    {
        StepType.Run => "run step",
        StepType.Sleep => "sleep",
        StepType.WaitForEvent => "wait for an event",
        StepType.Invoke => "invoke",
        StepType.SendEvent => "send step, whose events the host records",
        _ => type.ToString(),
    };

    private Exception TakeStepError(StepRecord step, JsonElement error) =>
        _stepErrors.Remove(step.HashedId, out var thrown) ? thrown : new StepFailedException(step.Id, ErrorSerializer.Read(error));

    // The result becomes part of every later call, in the order results arrived.
    private void Settle(StepRecord step, byte[] memo, TestStepStatus status)
    {
        _memo[step.HashedId] = memo;
        _stack.Add(step.HashedId);
        step.Status = status;

        // Inngest calls the function again once no step is pending, and after every step of a race.
        _pending.Remove(step.HashedId);
        if (_pending.Count == 0 || step.Races)
        {
            _forceStepPlan = false;
            QueueDiscovery(attempt: 0);
        }
    }

    private static JsonElement Parse(ReadOnlySpan<byte> json)
    {
        var reader = new Utf8JsonReader(json);
        return JsonElement.ParseValue(ref reader);
    }

    // What Inngest stores for a step that ran: {"data": <output>} or {"error": <error>}.
    private byte[] Wrap(string property, JsonElement value)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = WireWriter.Create(buffer, _host.SerializerOptions))
        {
            writer.WriteStartObject();
            writer.WritePropertyName(property);
            if (value.ValueKind == JsonValueKind.Undefined)
            {
                writer.WriteNullValue();
            }
            else
            {
                value.WriteTo(writer);
            }

            writer.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }

    private void Retry(Work work)
    {
        if (work.Kind == WorkKind.Discover)
        {
            QueueDiscovery(work.Attempt + 1);
        }
        else
        {
            Enqueue(work with { Attempt = work.Attempt + 1 }, _clock.Now);
        }
    }

    private void QueueDiscovery(int attempt)
    {
        if (!_discoveryQueued)
        {
            _discoveryQueued = true;
            Enqueue(new Work(WorkKind.Discover, attempt), _clock.Now);
        }
    }

    private void Enqueue(Work work, DateTimeOffset at) => _queue.Enqueue(work, (at, _sequence++));

    // When Inngest would end a sleep: after its duration, or at its time.
    private DateTimeOffset WakeTime(ReportedOp op)
    {
        var value = ReportedOp.Text(op.Options, "duration");
        if (TimeStrings.TryParseDuration(value, out var duration))
        {
            return _clock.Now + duration;
        }

        return TimeStrings.TryParseInstant(value, out var until) ? until : _clock.Now;
    }

    private static TimeSpan Timeout(ReportedOp op) =>
        TimeStrings.TryParseDuration(ReportedOp.Text(op.Options, "timeout"), out var timeout) ? timeout : TimeSpan.Zero;

    private bool ReachedStop()
    {
        if (_stopAt is null)
        {
            return false;
        }

        foreach (var step in _found)
        {
            if (step.Id == _stopAt)
            {
                // A step Inngest carries out is done once it is reported; a run step once it has run for good.
                return !step.Runnable || step.Status != TestStepStatus.Pending;
            }
        }

        return false;
    }

    private async Task<Answer> CallAsync(string? stepId, int attempt, CancellationToken cancellationToken)
    {
        if (++_calls > MaxCalls)
        {
            throw new InvalidOperationException(
                $"Function \"{_function.Id}\" was called {MaxCalls} times without finishing. The test host stops a run that keeps adding steps.");
        }

        _runError = null;
        _stepErrors.Clear();
        _mocked.Clear();

        using var body = new PooledBufferWriter();
        WriteBody(body, attempt);

        var scope = _host.CreateScope();
        await using (scope.ConfigureAwait(false))
        {
            Calling.Value = this;
            var request = new ExecutionRequest
            {
                Function = _function,
                Body = body.WrittenMemory,
                StepId = stepId ?? "step",
                Services = scope.ServiceProvider,
                RequestId = Ulid.New(_clock.Now),
            };

            using var result = await _host.Engine.ExecuteAsync(request, cancellationToken).ConfigureAwait(false);
            return new Answer(result.StatusCode, result.Body.WrittenSpan.ToArray(), result.NoRetry);
        }
    }

    // A call body as Inngest sends it: keys sorted, and the stack listing results in the order they completed.
    private void WriteBody(IBufferWriter<byte> body, int attempt)
    {
        using var writer = new Utf8JsonWriter(body);
        writer.WriteStartObject();
        writer.WriteStartObject("ctx");
        writer.WriteNumber("attempt", attempt);
        writer.WriteBoolean("disable_immediate_execution", _forceStepPlan);
        writer.WriteNumber("max_attempts", _maxAttempts);
        writer.WriteString("run_id", RunId);
        writer.WriteStartObject("stack");
        writer.WriteNumber("current", _stack.Count);
        writer.WriteStartArray("stack");
        foreach (var hashedId in _stack)
        {
            writer.WriteStringValue(hashedId);
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
        writer.WriteBoolean("use_api", false);
        writer.WriteEndObject();

        writer.WritePropertyName("event");
        writer.WriteRawValue(_events[0], skipInputValidation: true);
        writer.WriteStartArray("events");
        foreach (var @event in _events)
        {
            writer.WriteRawValue(@event, skipInputValidation: true);
        }

        writer.WriteEndArray();
        writer.WriteStartObject("steps");
        foreach (var (hashedId, memo) in _memo)
        {
            writer.WritePropertyName(hashedId);
            writer.WriteRawValue(memo, skipInputValidation: true);
        }

        writer.WriteEndObject();
        writer.WriteNumber("version", 2);
        writer.WriteEndObject();
    }

    private TestRun Fail(Exception error, byte[]? errorJson = null)
    {
        if (errorJson is null)
        {
            var buffer = new ArrayBufferWriter<byte>();
            using (var writer = WireWriter.Create(buffer, _host.SerializerOptions))
            {
                ErrorSerializer.Write(writer, error);
            }

            errorJson = buffer.WrittenSpan.ToArray();
        }

        return Finish(TestRunStatus.Failed, error: error, errorJson: errorJson);
    }

    // The SDK failed before the function could throw, for example on an event that does not fit the function's
    // event type, so only the error Inngest would record is left.
    private static InngestException Rebuild(byte[] body)
    {
        using var document = JsonDocument.Parse(body);
        var error = ErrorSerializer.Read(document.RootElement);
        return new InngestException($"{error.Name}: {error.Message}");
    }

    private TestRun Finish(TestRunStatus status, JsonElement? output = null, Exception? error = null, byte[]? errorJson = null)
    {
        var options = _host.SerializerOptions;
        var steps = new TestStep[_found.Count];
        var waitingOn = new List<TestStep>();
        for (var i = 0; i < steps.Length; i++)
        {
            steps[i] = _found[i].ToTestStep(options);
            if (status == TestRunStatus.Waiting && _pending.Contains(_found[i].HashedId))
            {
                waitingOn.Add(steps[i]);
            }
        }

        return new TestRun(RunId, _function.Id, status, output, error, steps, waitingOn, [.. _sent], options)
        {
            ErrorJson = errorJson,
        };
    }

    // One item of the run's queue: a call that finds the next steps, a targeted call that runs a planned step, or
    // the result a mock gives a sleep, wait or invoke.
    private readonly record struct Work(WorkKind Kind, int Attempt = 0, StepRecord? Step = null, byte[]? Memo = null, Exception? Error = null);

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
