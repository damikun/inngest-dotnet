// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using System.Text.Json;
using Inngest.Errors;
using Inngest.Internal;

namespace Inngest.Testing;

// What the function reported in an answer, op by op, recorded the way Inngest records it.
internal sealed partial class RunDriver
{
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
                Enqueue(new Work(WorkKind.Execute, Step: step, JobId: Ulid.New(_clock.Now)), _clock.Now);
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
                if (invoke.Function is { } function)
                {
                    var invoked = ReportedOp.Text(op.Options, "function_id");
                    var mocked = _host.CompositeIdOf(function);
                    if (!string.Equals(invoked, mocked, StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException(
                            $"Step \"{step.Id}\" invokes \"{invoked}\", but its mock answers for {function.Name}, which is \"{mocked}\".");
                    }
                }

                Enqueue(new Work(WorkKind.Complete, Step: step, Memo: invoke.ToMemo(_host.SerializerOptions), Error: invoke.Error), _clock.Now);
                break;

            case ReportedOp.WaitForSignal when mock is SignalMock { TimesOut: true }:
                step.Mocked = true;
                Enqueue(new Work(WorkKind.Complete, Step: step, Memo: Null), _clock.Now + Timeout(op));
                break;

            case ReportedOp.WaitForSignal when mock is SignalMock signal:
                step.Mocked = true;
                var name = ReportedOp.Text(op.Options, "signal") ?? string.Empty;
                Enqueue(new Work(WorkKind.Complete, Step: step, Memo: signal.ToMemo(name, _host.SerializerOptions)), _clock.Now);
                break;

            case ReportedOp.Gateway or ReportedOp.AIGateway when mock is GatewayMock gateway:
                step.Mocked = true;
                Enqueue(new Work(WorkKind.Complete, Step: step, Memo: gateway.ToMemo(_host.SerializerOptions)), _clock.Now);
                break;
        }
    }

    // A mock completes a sleep, wait, invoke, fetch or inference.
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
        step.Output = step.Type is StepType.Invoke or StepType.Fetch or StepType.AIInfer && memo.TryGetProperty("data", out var data) ? data : memo;
        Settle(step, work.Memo!, TestStepStatus.Completed);
        return null;
    }

    private StepRecord Track(ReportedOp op)
    {
        if (!_steps.TryGetValue(op.HashedId, out var step))
        {
            if (_mocks.Find(op.Id) is { } mock && (op.Type is not { } type || !mock.CanAnswer(type)))
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

    // Covers every step type, not just the supported ones, so a type the host learns to mock already reads well.
    internal static string Describe(StepType type) => type switch
    {
        StepType.Run => "run step",
        StepType.Sleep => "sleep",
        StepType.WaitForEvent => "wait for an event",
        StepType.Invoke => "invoke",
        StepType.SendEvent => "send step, whose events the host records",
        StepType.WaitForSignal => "wait for a signal",
        StepType.SendSignal => "send-signal step",
        StepType.Fetch => "fetch",
        StepType.AIInfer => "call to an AI model",
        StepType.AIWrap => "AI wrap step",
        StepType.RealtimePublish => "realtime publish",
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
}
