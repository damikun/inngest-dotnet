// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using System.Text.Json;
using Inngest.Configuration;
using Inngest.Functions;
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
internal sealed partial class Invocation
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
