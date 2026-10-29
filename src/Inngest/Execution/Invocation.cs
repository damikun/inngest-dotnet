// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Inngest.Errors;
using Inngest.Functions;
using Inngest.Internal;
using Inngest.Middleware;
using Inngest.Registration;
using Inngest.Steps;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Inngest.Execution;

// One call from Inngest. The function replays on a scheduler of its own. Memoized results are handed back one
// at a time, in the order the steps completed, and whenever the function is idle the invocation decides whether
// to execute a step, report the steps it found, or wait for the function's own I/O.
//
// The state below is touched only from the invocation's logical thread: RunAsync and the callbacks its scheduler
// runs. Steps called off the scheduler, from ConfigureAwait(false) or Task.Run code, are posted to it.
internal sealed class Invocation
{
    private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromDays(1);

    // Inngest has no op for a step that is not found, so a targeted call fails with a 500 after this long.
    public static readonly TimeSpan StepNotFoundTimeout = TimeSpan.FromSeconds(10);

    private const int MaxContinuationsPerTick = 100_000;
    private const int MaxStepsInNotFoundReport = 25;

    // The step whose handler runs in the current async flow. Steps cannot be nested.
    private static readonly AsyncLocal<string?> ExecutingStep = new();

    // The invocation whose code runs in the current async flow, for code that has no function context.
    private static readonly AsyncLocal<Invocation?> Running = new();

    private static readonly JsonEncodedText DataProperty = JsonEncodedText.Encode("data");
    private static readonly JsonEncodedText ErrorProperty = JsonEncodedText.Encode("error");

    private readonly InvocationScheduler _scheduler = new();
    private readonly Dictionary<string, FoundStep> _steps = new(StringComparer.Ordinal);

    // Ids used more than once: the next index to try and the step that used the id first.
    private readonly Dictionary<string, (int Next, FoundStep First)> _repeats = new(StringComparer.Ordinal);

    // Every step found in this invocation, and those found since the last decision.
    private readonly List<FoundStep> _found = [];
    private readonly List<FoundStep> _window = [];

    // What the middleware's next() returns for a step that does not complete in this invocation.
    private readonly TaskCompletionSource<object?> _never = new();

    private readonly InngestSettings _settings;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly RunRequest _run;
    private readonly IServiceProvider _services;
    private readonly string _functionId;
    private readonly string? _target;
    private readonly InngestFunction _function;
    private readonly FunctionContext _context;
    private readonly ILogger _runLogger;
    private readonly ReplaySafeLogger _functionLogger;
    private readonly MiddlewarePipeline? _pipeline;

    // Set when this call runs consecutive steps itself and saves them to Inngest as it goes.
    private readonly Checkpointer? _checkpoint;

    // Under checkpointing, the WrapStepAsync chains of new steps wait here for the result of running them in this call.
    private Dictionary<FoundStep, TaskCompletionSource<Task<object?>>>? _inlineResults;

    private CancellationToken _cancellationToken;
    private int _orderHead;
    private int _windowVersion;
    private int _unseen;
    private bool _memoizationEnded;
    private bool _memoizationObserversPending;
    private bool _warnedReuse;
    private long _waitingSince;
    private volatile bool _finished;

    // Results the run completed first that had not been reached when later ones were handed back.
    private HashSet<string>? _skipped;

    public Invocation(ExecutionEngine engine, ExecutionRequest request, RunRequest run, CancellationToken cancellationToken)
    {
        _settings = engine.Settings;
        _time = engine.TimeProvider;
        _logger = engine.Logger;
        _run = run;
        _services = request.Services;
        _functionId = request.Function.Id;
        _target = string.IsNullOrEmpty(request.StepId) || request.StepId == "step" ? null : request.StepId;
        _checkpoint = _target is null ? Checkpointer.Create(engine.Checkpoints, _settings, request, run, _time) : null;
        _unseen = run.Order.Count;
        _function = request.Function.Resolve(request.Services);
        _runLogger = engine.GetFunctionLogger(_functionId);
        _functionLogger = new ReplaySafeLogger(_runLogger, run.RunId, run.EventName);

        // Resolved before the context, because the events pass through the middleware's payload transforms.
        var middleware = MiddlewarePipeline.Resolve(request.Services, request.Function.Options.Middleware);
        var transforms = PayloadTransforms.Find(middleware);
        _context = _function.CreateContext(new FunctionContextSeed(
            _functionId,
            run.RunId,
            run.Attempt,
            run.MaxAttempts,
            run.Event,
            run.Events,
            new StepTools(this),
            _functionLogger,
            request.Services,
            _settings.SerializerOptions,
            cancellationToken)
        {
            RequestId = request.RequestId,
            JobId = request.JobId,
            Middleware = middleware,
            Transforms = transforms,
        });

        _pipeline = middleware.Length == 0 ? null : new MiddlewarePipeline(middleware, _context, _logger);
        Transforms = transforms is null ? null : new PayloadTransforms(transforms, _context, _settings.SerializerOptions);
    }

    public JsonSerializerOptions SerializerOptions => _settings.SerializerOptions;

    // The id the app registered the function class under, for invoking it by its class.
    public string FunctionIdOf(Type functionType) => _services.GetRequiredService<IFunctionRegistry>().FunctionIdOf(functionType);

    // Null unless some middleware of the call rewrites payloads; steps read and write their JSON through it.
    public PayloadTransforms? Transforms { get; }

    // The steps of the invocation running in the current async flow, or null where steps cannot be called: outside an
    // invocation, inside a step handler and in observer hooks.
    public static IStepTools? AmbientSteps =>
        Running.Value is { } invocation && ExecutingStep.Value is null && MiddlewarePipeline.RunningObserver is null
            ? invocation._context.Step
            : null;

    public async Task<ExecutionResult> RunAsync(CancellationToken cancellationToken)
    {
        _cancellationToken = cancellationToken;
        Running.Value = this;
        try
        {
            if (_unseen == 0)
            {
                await EndMemoizationAsync().ConfigureAwait(false);
            }

            if (_pipeline is not null && _run.Memo.Count == 0 && _run.Attempt == 0)
            {
                await _pipeline.OnRunStartAsync().ConfigureAwait(false);
            }

            _waitingSince = _time.GetTimestamp();
            var function = _scheduler.Run(StartFunction);
            var result = await LoopAsync(function).ConfigureAwait(false);
            WarnIfStepsWereMissed();
            return _checkpoint is { Count: > 0 } ? await SaveBeforeAnswerAsync(result).ConfigureAwait(false) : result;
        }
        finally
        {
            // Steps the abandoned function calls from now on are ignored and never complete.
            _finished = true;
            _scheduler.Close();
            _checkpoint?.Dispose();
        }
    }

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

    private async Task<ExecutionResult> LoopAsync(Task<object?> function)
    {
        while (true)
        {
            try
            {
                _scheduler.RunPending(MaxContinuationsPerTick);
            }
            catch (Exception exception)
            {
                // An async void method threw. Nothing else can observe the exception, so the run fails with it.
                return await RejectAsync(exception).ConfigureAwait(false);
            }

            if (_memoizationObserversPending)
            {
                await EndMemoizationAsync().ConfigureAwait(false);
            }

            if (function.IsCompleted)
            {
                return await CompleteAsync(function).ConfigureAwait(false);
            }

            if (TryResolveNext())
            {
                continue;
            }

            if (await DecideAsync().ConfigureAwait(false) is { } result)
            {
                return result;
            }

            if (!await WaitAsync(function).ConfigureAwait(false))
            {
                return await StepNotFoundAsync().ConfigureAwait(false);
            }
        }
    }

    private Task<object?> StartFunction() => _pipeline is null ? InvokeFunction() : _pipeline.WrapFunctionAsync(InvokeFunction);

    private Task<object?> InvokeFunction()
    {
        try
        {
            return _function.ExecuteCoreAsync(_context, _cancellationToken);
        }
        catch (Exception exception)
        {
            return Task.FromException<object?>(exception);
        }
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
        if (!_scheduler.IsCurrent)
        {
            _scheduler.Post(static state => ((FoundStep)state!).Owner.OnChainCompleted((FoundStep)state), step);
        }
        else if (!_finished)
        {
            step.CompleteFromChain();
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
            await _pipeline!.OnMemoizationEndAsync().ConfigureAwait(false);
        }
    }

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

    // Waits for more work, the end of the function or the delay; true when the delay ran out first.
    private async Task<bool> WaitAtMostAsync(Task work, Task function, TimeSpan delay)
    {
        // Under checkpointing the result of a step is usually queued already, and then no timer is needed.
        if (work.IsCompleted || function.IsCompleted)
        {
            return false;
        }

        using var timer = new CancellationTokenSource();
        var deadline = Task.Delay(delay, _time, timer.Token);
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
            await _pipeline.OnStepCompleteAsync(outcome.Info!, outcome.Output).ConfigureAwait(false);
        }

        // The function gets the result once the step is saved, or straight away while the buffer fills.
        step.Handled = true;
        _scheduler.Post(static state => ((FoundStep)state!).Owner.HandBack((FoundStep)state), step);
        ClearWindow();
        return _checkpoint!.IsDue ? await SaveAsync().ConfigureAwait(false) : null;
    }

    // Runs the step's handler after OnStepStartAsync, the same whether the call then answers or goes on.
    private async Task<StepOutcome> RunStepAsync(FoundStep step)
    {
        await EndMemoizationAsync().ConfigureAwait(false);
        var info = _pipeline is null ? null : step.CreateInfo(memoized: false);
        if (_pipeline is not null)
        {
            await _pipeline.OnStepStartAsync(info!).ConfigureAwait(false);
        }

        var startedAt = _time.GetUtcNow();
        var started = _time.GetTimestamp();
        object? output = null;
        Exception? error = null;
        try
        {
            output = await RunHandler(step, info).ConfigureAwait(false);
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
                    await _pipeline.OnStepCompleteAsync(outcome.Info!, outcome.Output).ConfigureAwait(false);
                }

                return new ExecutionResult(206, body);
            }

            var (retriable, retryAfter) = Classify(error, step: true);
            ReportStepError(step, error, retriable);
            if (_pipeline is not null)
            {
                await _pipeline.OnStepErrorAsync(outcome.Info!, error, isFinalAttempt: !retriable).ConfigureAwait(false);
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

    // Returns the exception when the output cannot be serialized, which then fails the step.
    private Exception? WriteStepRun(PooledBufferWriter body, FoundStep step, object? output, Timing timing)
    {
        try
        {
            using var writer = WireWriter.Create(body, _settings.SerializerOptions);
            writer.WriteStartArray();
            WriteStepRunOp(writer, step, output, timing);
            writer.WriteEndArray();
            return null;
        }
        catch (Exception exception)
        {
            body.Clear();
            return exception;
        }
    }

    // The one place a StepRun op is written, for the classic answer and the checkpoint alike, so the payload
    // transforms of the call's middleware apply to both.
    private void WriteStepRunOp(Utf8JsonWriter writer, FoundStep step, object? output, Timing timing)
    {
        step.WriteOpStart(writer, Ops.StepRun, _settings);
        writer.WritePropertyName(DataProperty);
        step.WriteData(writer, output, _settings.SerializerOptions);
        timing.Write(writer);
        writer.WriteEndObject();
    }

    private void WriteStepError(PooledBufferWriter body, FoundStep step, Exception error, bool final, Timing timing)
    {
        using var writer = WireWriter.Create(body, _settings.SerializerOptions);
        writer.WriteStartArray();
        step.WriteOpStart(writer, final ? Ops.StepFailed : Ops.StepError, _settings);
        writer.WritePropertyName(ErrorProperty);
        ErrorSerializer.Write(writer, error);
        timing.Write(writer);
        writer.WriteEndObject();
        writer.WriteEndArray();
    }

    private async ValueTask<ExecutionResult> ReportAsync(List<FoundStep> steps)
    {
        await EndMemoizationAsync().ConfigureAwait(false);
        var body = new PooledBufferWriter();
        try
        {
            using (var writer = WireWriter.Create(body, _settings.SerializerOptions))
            {
                writer.WriteStartArray();
                foreach (var step in steps)
                {
                    if (step.IsNew)
                    {
                        step.WriteOpStart(writer, step.Op, _settings);
                        writer.WriteEndObject();
                    }
                }

                writer.WriteEndArray();
            }

            return new ExecutionResult(206, body);
        }
        catch (Exception exception)
        {
            // Only user data can fail here, such as an invoke payload that cannot be serialized.
            body.Dispose();
            return await RejectAsync(exception).ConfigureAwait(false);
        }
    }

    private async ValueTask<ExecutionResult> CompleteAsync(Task<object?> function)
    {
        if (!function.IsCompletedSuccessfully)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            var error = function.IsFaulted ? function.Exception!.InnerException! : CancellationOf(function);
            return await RejectAsync(error).ConfigureAwait(false);
        }

        var output = function.Result;

        // Steps the function started but never awaited still have to run. They are reported instead of the
        // output, which the function returns again on a later call, and a requested step that is one of them
        // runs now even though the function did not wait for it.
        var requested = _target is null ? null : FindTarget(_found);
        if (requested is not null || (_target is null && _found.Exists(static step => step.IsNew)))
        {
            if (_pipeline is not null)
            {
                await _pipeline.OnRunCompleteAsync(output).ConfigureAwait(false);
            }

            return requested is null
                ? await ReportAsync(_found).ConfigureAwait(false)
                : await ExecuteAsync(requested).ConfigureAwait(false);
        }

        var body = new PooledBufferWriter();
        try
        {
            using var writer = WireWriter.Create(body, _settings.SerializerOptions);
            PayloadTransforms.WriteFunctionOutput(writer, output, _settings.SerializerOptions, Transforms);
        }
        catch (Exception exception)
        {
            body.Dispose();
            return await RejectAsync(exception).ConfigureAwait(false);
        }

        if (_pipeline is not null)
        {
            await _pipeline.OnRunCompleteAsync(output).ConfigureAwait(false);
        }

        return new ExecutionResult(200, body);
    }

    private async ValueTask<ExecutionResult> RejectAsync(Exception exception)
    {
        var (retriable, retryAfter) = Classify(exception, step: false);
        ReportRunError(exception, retriable);
        if (_pipeline is not null)
        {
            await _pipeline.OnRunErrorAsync(exception, isFinalAttempt: !retriable).ConfigureAwait(false);
        }

        var body = new PooledBufferWriter();
        using (var writer = WireWriter.Create(body, _settings.SerializerOptions))
        {
            ErrorSerializer.Write(writer, exception);
        }

        return new ExecutionResult(retriable ? 500 : 400, body) { NoRetry = !retriable, RetryAfter = retryAfter };
    }

    // The engine answers Inngest instead of letting user exceptions reach the host, so it reports them itself: in
    // the log, and on the call's activity for tracing.
    private void ReportRunError(Exception exception, bool retriable)
    {
        if (retriable)
        {
            Log.RunErrored(_runLogger, _run.RunId, _functionId, _run.Attempt, exception);
        }
        else
        {
            Log.RunFailed(_runLogger, _run.RunId, _functionId, _run.Attempt, exception);
        }

        RecordOnActivity(exception);
    }

    private void ReportStepError(FoundStep step, Exception exception, bool retriable)
    {
        if (retriable)
        {
            Log.StepErrored(_runLogger, step.FinalId, _run.RunId, _functionId, _run.Attempt, exception);
        }
        else
        {
            Log.StepFailed(_runLogger, step.FinalId, _run.RunId, _functionId, _run.Attempt, exception);
        }

        RecordOnActivity(exception);
    }

    private static void RecordOnActivity(Exception exception)
    {
        if (Activity.Current is { } activity)
        {
            activity.AddException(exception);
            activity.SetStatus(ActivityStatusCode.Error, exception.Message);
        }
    }

    private (bool Retriable, string? RetryAfter) Classify(Exception exception, bool step)
    {
        if (_run.MaxAttempts is { } maxAttempts && maxAttempts > 0 && _run.Attempt >= maxAttempts - 1)
        {
            return (false, null);
        }

        return exception switch
        {
            NonRetriableException => (false, null),

            // A step failure the function did not catch fails the run without retrying it.
            StepFailedException when !step => (false, null),
            RetryAfterException retryAfter => (true, FormatRetryAfter(retryAfter)),
            _ => (true, null),
        };
    }

    // Inngest caps retry delays at a day and reads only short numbers as seconds, so longer delays are capped here.
    private static string FormatRetryAfter(RetryAfterException exception) =>
        exception.RetryAt is { } retryAt
            ? TimeString.Format(retryAt)
            : ((long)Math.Ceiling(Math.Min(exception.Delay.GetValueOrDefault().TotalSeconds, MaxRetryDelay.TotalSeconds)))
                .ToString(CultureInfo.InvariantCulture);

    // A canceled task rethrows the exception that canceled it, which keeps the message and stack of, say, an
    // HttpClient timeout.
    private static Exception CancellationOf(Task task)
    {
        try
        {
            task.GetAwaiter().GetResult();
        }
        catch (OperationCanceledException exception)
        {
            return exception;
        }

        return new TaskCanceledException(task);
    }

    private async ValueTask<ExecutionResult> StepNotFoundAsync()
    {
        await EndMemoizationAsync().ConfigureAwait(false);
        Log.StepNotFound(_logger, _run.RunId, _functionId, _target!, StepNotFoundTimeout);

        var found = _found.FindAll(static step => step.IsNew);
        found.Sort(static (a, b) => string.CompareOrdinal(a.HashedId, b.HashedId));
        var shown = Math.Min(found.Count, MaxStepsInNotFoundReport);

        // The same diagnostics the TypeScript SDK sends.
        var message = new StringBuilder();
        message.Append(CultureInfo.InvariantCulture, $"Could not find step \"{_target}\" to run; timed out.");
        if (shown > 0)
        {
            message.Append(" Found new steps: \n");
            for (var i = 0; i < shown; i++)
            {
                message.Append(CultureInfo.InvariantCulture, $"{(i > 0 ? "\n" : string.Empty)}{found[i].DisplayName} ({found[i].HashedId})");
            }

            message.Append('.');
        }

        if (found.Count > shown)
        {
            message.Append(CultureInfo.InvariantCulture, $" (showing {shown} of {found.Count})");
        }

        var body = new PooledBufferWriter();
        using (var writer = WireWriter.Create(body, _settings.SerializerOptions))
        {
            writer.WriteStartObject();
            writer.WriteString("error", message.ToString());
            writer.WriteString("requestedStep", _target);
            writer.WriteStartArray("foundSteps");
            for (var i = 0; i < shown; i++)
            {
                writer.WriteStartObject();
                writer.WriteString("id", found[i].HashedId);
                if (found[i].GetName() is { } name)
                {
                    writer.WriteString("name", name);
                }

                writer.WriteString("displayName", found[i].DisplayName);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteNumber("totalFoundSteps", found.Count);
            writer.WriteEndObject();
        }

        return new ExecutionResult(500, body) { NoRetry = false };
    }

    // What running a step's handler gave: its output or the exception it threw, and when it ran.
    private readonly record struct StepOutcome(StepInfo? Info, object? Output, Exception? Error, Timing Timing);

    // When a step ran, as Go's interval.Interval: start in Unix nanoseconds and duration in nanoseconds.
    private readonly record struct Timing(DateTimeOffset Start, TimeSpan Duration)
    {
        private static readonly JsonEncodedText TimingProperty = JsonEncodedText.Encode("timing");
        private static readonly JsonEncodedText StartProperty = JsonEncodedText.Encode("a");
        private static readonly JsonEncodedText DurationProperty = JsonEncodedText.Encode("b");

        public void Write(Utf8JsonWriter writer)
        {
            writer.WriteStartObject(TimingProperty);
            writer.WriteNumber(StartProperty, (Start - DateTimeOffset.UnixEpoch).Ticks * TimeSpan.NanosecondsPerTick);
            writer.WriteNumber(DurationProperty, Duration.Ticks * TimeSpan.NanosecondsPerTick);
            writer.WriteEndObject();
        }
    }
}
