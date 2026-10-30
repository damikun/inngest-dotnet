// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using System.Net;
using System.Text.Json;
using Inngest.Errors;
using Inngest.Internal;

namespace Inngest.Execution;

// Deciding what the call does with the steps it found, and running a step in this call, checkpointed or not.
internal sealed partial class Invocation
{
    // Runs when the function is idle and no memoized result can be handed back. Null means keep waiting.
    private async ValueTask<ExecutionResult?> DecideAsync()
    {
        // Buffered steps that have waited MaxInterval are saved, even while the function waits on its own work.
        if (_checkpoint is { IsDue: true } && await SaveAsync().ConfigureAwait(false) is { } failed)
        {
            return failed;
        }

        if (_window.Count == 0)
        {
            return null;
        }

        if (_target is not null)
        {
            if (FindTarget(_window) is { } target)
            {
                return await ExecuteAsync(target).ConfigureAwait(false);
            }

            // Other new steps are ignored: Inngest has planned them already and runs each in a call of its own.
            ClearWindow();
            _waitingSince = _time.GetTimestamp();
            return null;
        }

        FoundStep? unfulfilled = null;
        var unfulfilledCount = 0;
        var hasNew = false;
        foreach (var step in _window)
        {
            if (!step.Fulfilled)
            {
                unfulfilled = step;
                unfulfilledCount++;
            }

            hasNew |= step.IsNew;
        }

        // A single new run step executes right away, saving Inngest a round trip. Under checkpointing the function
        // then goes on in this call.
        if (unfulfilledCount == 1 && CanRunInline(unfulfilled!))
        {
            return _checkpoint is null
                ? await ExecuteAsync(unfulfilled!).ConfigureAwait(false)
                : await ExecuteAndContinueAsync(unfulfilled!).ConfigureAwait(false);
        }

        if (!hasNew)
        {
            // Only memoized steps: the function is waiting on something of its own, such as I/O.
            ClearWindow();
            return null;
        }

        return await ReportAsync(_window).ConfigureAwait(false);
    }

    // Whether a lone new step may run in this call instead of being planned for a call of its own. A step of a race
    // is always planned: once another step of the race finished, Inngest may be running this one in another call.
    // Past the checkpointing MaxRuntime the step is left to Inngest, which starts a new call for it.
    private bool CanRunInline(FoundStep step) =>
        step.IsRunnable
        && step.ParallelMode != ParallelMode.Race
        && !_run.DisableImmediateExecution
        && _checkpoint is not { RuntimeExceeded: true };

    private FoundStep? FindTarget(List<FoundStep> steps)
    {
        foreach (var step in steps)
        {
            if (step.IsRunnable && step.HashedId == _target)
            {
                return step;
            }
        }

        return null;
    }

    private async Task<bool> WaitAsync(Task function)
    {
        var work = _scheduler.WaitForWorkAsync(_cancellationToken).AsTask();
        if (_target is null)
        {
            // Buffered steps wait at most MaxInterval to be saved, however long the function's own work takes.
            if (_checkpoint?.TimeUntilDue is { } due)
            {
                await WaitAtMostAsync(work, function, due).ConfigureAwait(false);
            }
            else
            {
                await Task.WhenAny(work, function).ConfigureAwait(false);
            }

            _cancellationToken.ThrowIfCancellationRequested();
            return true;
        }

        // A targeted call fails once the step has not shown up for a while.
        var remaining = StepNotFoundTimeout - _time.GetElapsedTime(_waitingSince);
        if (remaining <= TimeSpan.Zero)
        {
            return false;
        }

        var expired = await WaitAtMostAsync(work, function, remaining).ConfigureAwait(false);
        _cancellationToken.ThrowIfCancellationRequested();
        return !expired;
    }

    // Waits for more work, the end of the function or the delay; true when the delay ran out first. A delay longer than
    // a timer allows, such as a MaxInterval of months, is cut short, and the caller then simply checks again.
    private async Task<bool> WaitAtMostAsync(Task work, Task function, TimeSpan delay)
    {
        // Under checkpointing the result of a step is usually queued already, and then no timer is needed.
        if (work.IsCompleted || function.IsCompleted)
        {
            return false;
        }

        using var timer = new CancellationTokenSource();
        var deadline = Task.Delay(delay < MaxTimerDelay ? delay : MaxTimerDelay, _time, timer.Token);
        var completed = await Task.WhenAny(work, function, deadline).ConfigureAwait(false);
        await timer.CancelAsync().ConfigureAwait(false);
        return completed == deadline;
    }

    private async Task<ExecutionResult> ExecuteAsync(FoundStep step) =>
        await AnswerAsync(step, await RunStepAsync(step).ConfigureAwait(false)).ConfigureAwait(false);

    // Under checkpointing: runs the step, buffers it for the next checkpoint and returns null, so that the function
    // goes on in this call. A step error, and a call that retries an earlier one, are answered as without checkpointing.
    private async Task<ExecutionResult?> ExecuteAndContinueAsync(FoundStep step)
    {
        // The steps this decision was about. Steps the function finds while the handler runs come after them.
        var decided = _window.Count;
        var outcome = await RunStepAsync(step).ConfigureAwait(false);

        // A retry answers after its first successful step, as inngest-js does, so that Inngest counts the attempts of
        // the next step from zero instead of carrying this call's attempt over to it.
        if (outcome.Error is not null || _run.Attempt > 0)
        {
            return await AnswerAsync(step, outcome).ConfigureAwait(false);
        }

        if (Buffer(step, outcome) is { } error)
        {
            return await AnswerAsync(step, outcome with { Error = error }).ConfigureAwait(false);
        }

        if (_pipeline is not null)
        {
            await AwaitHookAsync(_pipeline.OnStepCompleteAsync(outcome.Info!, outcome.Output)).ConfigureAwait(false);
        }

        // The function gets the result once the step is saved, or straight away while the buffer fills.
        step.Handled = true;
        _scheduler.Post(static state => ((FoundStep)state!).Owner.HandBack((FoundStep)state), step);

        // Steps found meanwhile wait for the next decision. The window keeps its version, as during a replay, so that a
        // loop whose steps all run in this call does not look like parallel branches sharing an id.
        _window.RemoveRange(0, decided);
        return _checkpoint!.IsDue ? await SaveAsync().ConfigureAwait(false) : null;
    }

    // Runs the step's handler after OnStepStartAsync, the same whether the call then answers or goes on.
    private async Task<StepOutcome> RunStepAsync(FoundStep step)
    {
        await EndMemoizationAsync().ConfigureAwait(false);
        var info = _pipeline is null ? null : step.CreateInfo(memoized: false);
        if (_pipeline is not null)
        {
            await AwaitHookAsync(_pipeline.OnStepStartAsync(info!)).ConfigureAwait(false);
        }

        var startedAt = _time.GetUtcNow();
        var started = _time.GetTimestamp();
        object? output = null;
        Exception? error = null;
        try
        {
            output = await AwaitHandlerAsync(RunHandler(step, info)).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            error = exception;
        }

        return new StepOutcome(info, output, error, new Timing(startedAt, _time.GetElapsedTime(started)));
    }

    private async Task<object?> AwaitHandlerAsync(Task<object?> handler)
    {
        await RunPendingUntilAsync(handler).ConfigureAwait(false);
        return await handler.ConfigureAwait(false);
    }

    // A middleware hook may wait on the same tasks as a handler, such as a token a singleton caches that the function
    // started loading, so it is awaited the same way.
    private async ValueTask AwaitHookAsync(ValueTask hook)
    {
        if (hook.IsCompleted)
        {
            await hook.ConfigureAwait(false);
            return;
        }

        var task = hook.AsTask();
        await RunPendingUntilAsync(task).ConfigureAwait(false);
        await task.ConfigureAwait(false);
    }

    // The function's own continuations keep running while a handler or a hook runs, as on the JavaScript event loop:
    // that code may await a task the function started, and that task resumes on the scheduler. Nothing is decided
    // meanwhile. A cancelled call stops waiting even for code that ignores the token.
    private async Task RunPendingUntilAsync(Task task)
    {
        while (!task.IsCompleted)
        {
            var work = _scheduler.WaitForWorkAsync(_cancellationToken).AsTask();
            await Task.WhenAny(task, work).ConfigureAwait(false);
            _cancellationToken.ThrowIfCancellationRequested();
            try
            {
                _scheduler.RunPending(MaxContinuationsPerTick);
            }
            catch (Exception)
            {
                // An async void method threw. RunPending throws it again each time it runs, so the loop fails the run
                // with it once the step is done, or the next call does.
            }
        }
    }

    // Answers with the step's op: StepRun, or StepError or StepFailed when it threw.
    private async Task<ExecutionResult> AnswerAsync(FoundStep step, StepOutcome outcome)
    {
        var body = new PooledBufferWriter();
        try
        {
            var error = outcome.Error ?? WriteStepRun(body, step, outcome.Output, outcome.Timing);
            if (error is null)
            {
                if (_pipeline is not null)
                {
                    await AwaitHookAsync(_pipeline.OnStepCompleteAsync(outcome.Info!, outcome.Output)).ConfigureAwait(false);
                }

                return new ExecutionResult(206, body);
            }

            var (retriable, retryAfter) = Classify(error, step: true);
            ReportStepError(step, error, retriable);
            if (_pipeline is not null)
            {
                await AwaitHookAsync(_pipeline.OnStepErrorAsync(outcome.Info!, error, isFinalAttempt: !retriable)).ConfigureAwait(false);
            }

            WriteStepError(body, step, error, final: !retriable, outcome.Timing);
            return new ExecutionResult(206, body) { NoRetry = !retriable, RetryAfter = retryAfter };
        }
        catch
        {
            body.Dispose();
            throw;
        }
    }

    // Adds the step's StepRun op to the checkpoint and gives the step the result a later call would read back.
    // Returns the exception when the output cannot be serialized, which then fails the step.
    private Exception? Buffer(FoundStep step, StepOutcome outcome)
    {
        using var op = new PooledBufferWriter();
        JsonElement data;
        try
        {
            using (var writer = WireWriter.Create(op, _settings.SerializerOptions))
            {
                WriteStepRunOp(writer, step, outcome.Output, outcome.Timing);
            }

            data = _checkpoint!.Add(op.WrittenSpan);
        }
        catch (Exception exception)
        {
            return exception;
        }

        step.Attach(step.FinalId, step.Index, step.HashedId, new MemoEntry(MemoKind.Data, data));
        return null;
    }

    // Hands a step that ran in this call its result. A new step found with middleware has its WrapStepAsync chain
    // waiting in Discover; any other resolves the way a memoized step does.
    private void HandBack(FoundStep step)
    {
        if (_finished)
        {
            return;
        }

        if (_inlineResults is not null && _inlineResults.Remove(step, out var inline))
        {
            inline.TrySetResult(ReadMemo(step));
        }
        else
        {
            Resolve(step);
        }
    }

    // Saves the buffered steps. Null when Inngest saved them; otherwise the answer that ends the call.
    private async Task<ExecutionResult?> SaveAsync()
    {
        var count = _checkpoint!.Count;
        try
        {
            await _checkpoint.SaveAsync(_cancellationToken).ConfigureAwait(false);
            Log.Checkpointed(_logger, count, _run.RunId, _functionId);
            return null;
        }
        catch (InngestException exception) when (exception.StatusCode == (int)HttpStatusCode.Conflict)
        {
            _checkpoint.Discard();
            return StaleDispatch(exception);
        }
        catch (Exception exception) when (!_cancellationToken.IsCancellationRequested)
        {
            // As the SDK spec asks, Inngest then saves the steps from the answer and calls again.
            Log.CheckpointFailed(_logger, count, _run.RunId, _functionId, exception);
            return _checkpoint.TakeAnswer();
        }
    }

    // Steps still buffered are saved before Inngest gets the answer. When that fails, the answer is the steps instead,
    // and the function reaches its answer again in the next call.
    private async Task<ExecutionResult> SaveBeforeAnswerAsync(ExecutionResult answer)
    {
        ExecutionResult? failed;
        try
        {
            failed = await SaveAsync().ConfigureAwait(false);
        }
        catch
        {
            answer.Dispose();
            throw;
        }

        if (failed is null)
        {
            return answer;
        }

        answer.Dispose();
        return failed;
    }

    // Inngest has dispatched the run again since this call started, and that newer call owns it now. Like inngest-js,
    // this call stops without a retry instead of running steps the newer one runs too.
    private ExecutionResult StaleDispatch(InngestException exception)
    {
        Log.StaleDispatch(_logger, _run.RunId, _functionId, exception);
        var body = new PooledBufferWriter();
        using (var writer = WireWriter.Create(body, _settings.SerializerOptions))
        {
            ErrorSerializer.Write(writer, exception);
        }

        return new ExecutionResult(400, body) { NoRetry = true };
    }

    // Step handlers run off the scheduler, so their own awaits do not wait for it.
    private Task<object?> RunHandler(FoundStep step, StepInfo? info)
    {
        var synchronizationContext = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(null);
        ExecutingStep.Value = step.FinalId;
        try
        {
            return _pipeline is null
                ? step.ExecuteAsync(_context, _services, _cancellationToken)
                : _pipeline.WrapStepHandlerAsync(info!, () => step.ExecuteAsync(_context, _services, _cancellationToken));
        }
        catch (Exception exception)
        {
            return Task.FromException<object?>(exception);
        }
        finally
        {
            ExecutingStep.Value = null;
            SynchronizationContext.SetSynchronizationContext(synchronizationContext);
        }
    }
}
