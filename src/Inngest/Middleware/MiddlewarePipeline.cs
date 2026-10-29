// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using Inngest.Execution;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Inngest.Middleware;

// The middleware of one invocation, in registration order. Observer hooks run one after another and a failing
// observer is logged and skipped. Wrappers nest with the first registered middleware outermost, and their
// exceptions propagate.
internal sealed class MiddlewarePipeline(InngestMiddleware[] middleware, FunctionContext context, ILogger logger)
{
    // The middleware whose WrapStepAsync is running in the current async branch. A step created there skips that
    // middleware, so a middleware can call step tools from its wrapper without wrapping its own steps forever.
    private static readonly AsyncLocal<ActiveWrap?> Active = new();

    // The observer hook running in the current async flow. The engine awaits observers before it runs the
    // function again, so a step awaited inside one could never complete. Async methods do not pass changes to
    // it back to their caller, so it does not outlive the hook.
    private static readonly AsyncLocal<string?> Observer = new();

    public static ActiveWrap? ActiveWraps => Active.Value;

    public static string? RunningObserver => Observer.Value;

    // The middleware of one call: the app's in registration order, then the function's own, so that app middleware
    // wraps the function's, as in the TypeScript SDK.
    public static InngestMiddleware[] Resolve(IServiceProvider services, IReadOnlyList<FunctionMiddleware>? forFunction)
    {
        var app = services.GetServices<InngestMiddleware>();
        if (forFunction is not { Count: > 0 })
        {
            return [.. app];
        }

        List<InngestMiddleware> all = [.. app];
        foreach (var item in forFunction)
        {
            all.Add(item.Resolve(services));
        }

        return [.. all];
    }

    public ValueTask OnRunStartAsync() =>
        ObserveAsync(nameof(InngestMiddleware.OnRunStartAsync), default(object), static (item, context, _) => item.OnRunStartAsync(context));

    public ValueTask OnMemoizationEndAsync() =>
        ObserveAsync(nameof(InngestMiddleware.OnMemoizationEndAsync), default(object), static (item, context, _) => item.OnMemoizationEndAsync(context));

    public ValueTask OnRunCompleteAsync(object? output) =>
        ObserveAsync(nameof(InngestMiddleware.OnRunCompleteAsync), output, static (item, context, output) => item.OnRunCompleteAsync(context, output));

    public ValueTask OnRunErrorAsync(Exception error, bool isFinalAttempt) =>
        ObserveAsync(
            nameof(InngestMiddleware.OnRunErrorAsync),
            (error, isFinalAttempt),
            static (item, context, state) => item.OnRunErrorAsync(context, state.error, state.isFinalAttempt));

    public ValueTask OnStepStartAsync(StepInfo step) =>
        ObserveAsync(nameof(InngestMiddleware.OnStepStartAsync), step, static (item, context, step) => item.OnStepStartAsync(context, step));

    public ValueTask OnStepCompleteAsync(StepInfo step, object? output) =>
        ObserveAsync(
            nameof(InngestMiddleware.OnStepCompleteAsync),
            (step, output),
            static (item, context, state) => item.OnStepCompleteAsync(context, state.step, state.output));

    public ValueTask OnStepErrorAsync(StepInfo step, Exception error, bool isFinalAttempt) =>
        ObserveAsync(
            nameof(InngestMiddleware.OnStepErrorAsync),
            (step, error, isFinalAttempt),
            static (item, context, state) => item.OnStepErrorAsync(context, state.step, state.error, state.isFinalAttempt));

    public Task<object?> WrapFunctionAsync(Func<Task<object?>> function) => WrapFunction(0, function);

    public Task<object?> WrapStepHandlerAsync(StepInfo step, Func<Task<object?>> handler) => WrapStepHandler(0, step, handler);

    // resolve produces the value the function receives. active is what ActiveWraps was where the step was
    // created; those middleware are skipped.
    public Task<object?> WrapStepAsync(StepInfo step, Func<Task<object?>> resolve, ActiveWrap? active) =>
        WrapStep(0, step, resolve, active);

    private Task<object?> WrapFunction(int index, Func<Task<object?>> function)
    {
        if (index == middleware.Length)
        {
            return Invoke(function);
        }

        var item = middleware[index];
        return Invoke(() => item.WrapFunctionAsync(context, () => WrapFunction(index + 1, function)));
    }

    private Task<object?> WrapStepHandler(int index, StepInfo step, Func<Task<object?>> handler)
    {
        if (index == middleware.Length)
        {
            return Invoke(handler);
        }

        var item = middleware[index];
        return Invoke(() => item.WrapStepHandlerAsync(context, step, () => WrapStepHandler(index + 1, step, handler)));
    }

    private Task<object?> WrapStep(int index, StepInfo step, Func<Task<object?>> resolve, ActiveWrap? active)
    {
        while (index < middleware.Length && active is not null && active.Contains(middleware[index]))
        {
            index++;
        }

        if (index == middleware.Length)
        {
            return Invoke(resolve);
        }

        var item = middleware[index];
        var next = index + 1;

        // Inside next() the middleware is no longer active, so only steps it creates itself skip it.
        return WithActive(
            new ActiveWrap(item, active),
            () => item.WrapStepAsync(context, step, () => WithActive(active, () => WrapStep(next, step, resolve, active))));
    }

    private static Task<object?> WithActive(ActiveWrap? value, Func<Task<object?>> call)
    {
        var previous = Active.Value;
        Active.Value = value;
        try
        {
            return Invoke(call);
        }
        finally
        {
            Active.Value = previous;
        }
    }

    private static Task<object?> Invoke(Func<Task<object?>> call)
    {
        try
        {
            return call();
        }
        catch (Exception exception)
        {
            return Task.FromException<object?>(exception);
        }
    }

    // Runs one observer hook of every middleware in turn. A failing hook is logged and the others still run.
    private async ValueTask ObserveAsync<TState>(string hook, TState state, Func<InngestMiddleware, FunctionContext, TState, ValueTask> call)
    {
        Observer.Value = hook;
        foreach (var item in middleware)
        {
            try
            {
                await call(item, context, state).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                Log.MiddlewareFailed(logger, item.GetType().Name, hook, exception);
            }
        }
    }
}
