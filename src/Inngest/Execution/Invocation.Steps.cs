// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using System.Globalization;
using Inngest.Middleware;

namespace Inngest.Execution;

// Finding steps: claiming their ids, handing memoized results back in completion order, and noticing when memoization
// ends.
internal sealed partial class Invocation
{
    // Called by the step tools on whatever thread the function is on.
    public void Register(FoundStep step)
    {
        if (ExecutingStep.Value is { } running)
        {
            throw new InvalidOperationException(
                $"Step \"{step.Id}\" was called inside the handler of step \"{running}\". Steps cannot be nested; call them from the function instead.");
        }

        if (MiddlewarePipeline.RunningObserver is { } hook)
        {
            throw new InvalidOperationException(
                $"Step \"{step.Id}\" was called from {hook}. Middleware can call steps only from WrapFunctionAsync and WrapStepAsync.");
        }

        if (_finished)
        {
            return;
        }

        step.Owner = this;
        if (_pipeline is not null)
        {
            step.Guard = MiddlewarePipeline.ActiveWraps;
        }

        if (_scheduler.IsCurrent)
        {
            Add(step);
        }
        else
        {
            _scheduler.Post(static state => ((FoundStep)state!).Owner.Add((FoundStep)state), step);
        }
    }

    // Runs the action on the invocation's scheduler, with the execution context of the caller: at once when the caller is on
    // the scheduler already, otherwise queued there, as the steps of code that left the scheduler are. For code that finds
    // steps and goes on as their results are handed back, such as the lanes of a fan-out, since the engine can only see
    // code that runs on its scheduler.
    public void RunOnScheduler(Action action)
    {
        if (_scheduler.IsCurrent)
        {
            action();
            return;
        }

        _scheduler.Post(
            static state =>
            {
                var (action, context) = ((Action, ExecutionContext?))state!;
                if (context is null)
                {
                    action();
                }
                else
                {
                    ExecutionContext.Run(context, static action => ((Action)action!)(), action);
                }
            },
            (action, ExecutionContext.Capture()));
    }

    private void Add(FoundStep step)
    {
        if (_finished)
        {
            return;
        }

        var (finalId, index, hashedId) = ClaimId(step.Id);
        MemoEntry? memo = _run.Memo.TryGetValue(hashedId, out var entry) ? entry : null;
        step.Attach(finalId, index, hashedId, memo);
        _steps.Add(hashedId, step);
        _found.Add(step);
        if (memo is { Ordered: true } && --_unseen == 0)
        {
            EndMemoization();
        }

        if (_pipeline is not null && memo is null && step.IsRunnable)
        {
            // A new step is reported from the center of the WrapStepAsync chain, as in the TypeScript SDK, so a
            // middleware that fails or runs steps of its own before calling next() decides whether it is.
            Observe(step, _pipeline.WrapStepAsync(step.CreateInfo(memoized: false), () => Discover(step), step.Guard));
            return;
        }

        AddToWindow(step);
    }

    // Repeated ids get ":1", ":2", ... in the order they are called, which is stable across replays.
    private (string FinalId, int? Index, string HashedId) ClaimId(string id)
    {
        if (!_repeats.TryGetValue(id, out var repeat))
        {
            var hashed = StepIds.Hash(id);
            if (!_steps.TryGetValue(hashed, out var first))
            {
                return (id, null, hashed);
            }

            repeat = (1, first);
        }

        WarnIfReused(id, repeat.First);
        for (var index = repeat.Next; ; index++)
        {
            var candidate = string.Create(CultureInfo.InvariantCulture, $"{id}:{index}");
            var hashed = StepIds.Hash(candidate);
            if (!_steps.ContainsKey(hashed))
            {
                _repeats[id] = (index + 1, repeat.First);
                return (candidate, index, hashed);
            }
        }
    }

    // Reusing an id in a loop is fine. Reusing it after the first use was already decided on usually means that
    // parallel branches share an id, and then which branch gets which result depends on timing.
    private void WarnIfReused(string id, FoundStep first)
    {
        if (_warnedReuse || first.Window == _windowVersion)
        {
            return;
        }

        _warnedReuse = true;
        Log.StepIdReused(_logger, _functionId, id, _run.RunId);
    }

    // Hands back the next memoized result in completion order, so that races replay the way they first went.
    private bool TryResolveNext()
    {
        var order = _run.Order;
        while (_orderHead < order.Count && _steps.TryGetValue(order[_orderHead], out var head) && head.Handled)
        {
            _orderHead++;
        }

        for (var i = _orderHead; i < order.Count; i++)
        {
            if (!_steps.TryGetValue(order[i], out var step) || step.Handled)
            {
                continue;
            }

            // The step that completed first has not been reached. Waiting for it could hang the run, so the
            // earliest step that was reached goes first.
            if (i > _orderHead)
            {
                (_skipped ??= new HashSet<string>(StringComparer.Ordinal)).Add(order[_orderHead]);
            }

            step.Handled = true;
            _scheduler.Post(static state => ((FoundStep)state!).Owner.Resolve((FoundStep)state), step);
            return true;
        }

        return false;
    }

    // A skipped step that was reached later only came late, for example from code on another thread. One that
    // was never reached means the function no longer calls steps the way the run recorded.
    private void WarnIfStepsWereMissed()
    {
        if (_skipped is null)
        {
            return;
        }

        foreach (var hashedId in _skipped)
        {
            if (!_steps.ContainsKey(hashedId))
            {
                Log.StepOrderChanged(_logger, _functionId, hashedId, _run.RunId);
                return;
            }
        }
    }

    private void Resolve(FoundStep step)
    {
        if (_finished)
        {
            return;
        }

        if (_pipeline is null)
        {
            step.Resolve(_settings.SerializerOptions);
            return;
        }

        Observe(step, _pipeline.WrapStepAsync(step.CreateInfo(memoized: true), () => ReadMemo(step), step.Guard));
    }

    // The center of the chain for a memoized step. The request document may only be read on the scheduler.
    private Task<object?> ReadMemo(FoundStep step)
    {
        if (_scheduler.IsCurrent)
        {
            return _finished ? _never.Task : step.ReadMemoAsync(_settings.SerializerOptions);
        }

        var read = new TaskCompletionSource<object?>();
        _scheduler.Post(
            _ =>
            {
                if (_finished)
                {
                    return;
                }

                var memo = step.ReadMemoAsync(_settings.SerializerOptions);
                if (memo.IsCompletedSuccessfully)
                {
                    read.TrySetResult(memo.Result);
                }
                else
                {
                    read.TrySetException(memo.Exception!.InnerExceptions);
                }
            },
            null);
        return read.Task;
    }

    // The center of the chain for a new step: report it, and never complete in this invocation, unless
    // checkpointing runs the step in this call and hands its result back here.
    private Task<object?> Discover(FoundStep step)
    {
        if (_checkpoint is not null)
        {
            var inline = new TaskCompletionSource<Task<object?>>(step);
            if (_scheduler.IsCurrent)
            {
                AwaitInline(inline);
            }
            else
            {
                _scheduler.Post(
                    static state =>
                    {
                        var inline = (TaskCompletionSource<Task<object?>>)state!;
                        ((FoundStep)inline.Task.AsyncState!).Owner.AwaitInline(inline);
                    },
                    inline);
            }

            return inline.Task.Unwrap();
        }

        if (_scheduler.IsCurrent)
        {
            AddToWindow(step);
        }
        else
        {
            _scheduler.Post(static state => ((FoundStep)state!).Owner.AddToWindow((FoundStep)state), step);
        }

        return _never.Task;
    }

    private void AwaitInline(TaskCompletionSource<Task<object?>> inline)
    {
        var step = (FoundStep)inline.Task.AsyncState!;
        if (!_finished)
        {
            (_inlineResults ??= [])[step] = inline;
            AddToWindow(step);
        }
    }

    private void AddToWindow(FoundStep step)
    {
        if (!_finished)
        {
            step.Window = _windowVersion;
            _window.Add(step);
        }
    }

    private void ClearWindow()
    {
        _window.Clear();
        _windowVersion++;
    }

    // Completes the function's task with what the WrapStepAsync chain produced, on the scheduler.
    private void Observe(FoundStep step, Task<object?> chain)
    {
        step.Chain = chain;
        if (chain.IsCompleted)
        {
            step.CompleteFromChain();
            return;
        }

        chain.ContinueWith(
            static (_, state) => ((FoundStep)state!).Owner.OnChainCompleted((FoundStep)state),
            step,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private void OnChainCompleted(FoundStep step)
    {
        // Checked first: once the call has answered, posted work runs on the thread pool, where the scheduler is never
        // current, so a chain that completes after the answer would post itself forever.
        if (_finished)
        {
            return;
        }

        if (_scheduler.IsCurrent)
        {
            step.CompleteFromChain();
        }
        else
        {
            _scheduler.Post(static state => ((FoundStep)state!).Owner.OnChainCompleted((FoundStep)state), step);
        }
    }

    private void EndMemoization()
    {
        if (_memoizationEnded)
        {
            return;
        }

        _memoizationEnded = true;
        _functionLogger.Enable();
        _memoizationObserversPending = _pipeline is not null;
    }

    private async ValueTask EndMemoizationAsync()
    {
        EndMemoization();
        if (_memoizationObserversPending)
        {
            _memoizationObserversPending = false;
            await AwaitHookAsync(_pipeline!.OnMemoizationEndAsync()).ConfigureAwait(false);
        }
    }
}
