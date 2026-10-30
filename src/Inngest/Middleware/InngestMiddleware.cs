// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

namespace Inngest;

/// <summary>
/// Hooks into function runs, steps and event sending. Override only what you need; every hook defaults to doing
/// nothing. Middleware runs in registration order, so the first registered wrapper is the outermost one.
/// </summary>
public abstract class InngestMiddleware
{
    public virtual ValueTask OnRunStartAsync(FunctionContext context) => ValueTask.CompletedTask;

    /// <summary>Called once all memoized steps have been replayed and the run reaches new code.</summary>
    public virtual ValueTask OnMemoizationEndAsync(FunctionContext context) => ValueTask.CompletedTask;

    public virtual ValueTask OnRunCompleteAsync(FunctionContext context, object? output) => ValueTask.CompletedTask;

    public virtual ValueTask OnRunErrorAsync(FunctionContext context, Exception exception, bool isFinalAttempt) =>
        ValueTask.CompletedTask;

    /// <summary>
    /// Called before a step handler executes. Memoized steps do not execute and do not raise step events.
    /// </summary>
    public virtual ValueTask OnStepStartAsync(FunctionContext context, StepInfo step) => ValueTask.CompletedTask;

    public virtual ValueTask OnStepCompleteAsync(FunctionContext context, StepInfo step, object? output) =>
        ValueTask.CompletedTask;

    public virtual ValueTask OnStepErrorAsync(FunctionContext context, StepInfo step, Exception exception, bool isFinalAttempt) =>
        ValueTask.CompletedTask;

    /// <summary>Wraps the function body on every invocation, including replays.</summary>
    public virtual Task<object?> WrapFunctionAsync(FunctionContext context, Func<Task<object?>> next) => next();

    /// <summary>Wraps how a step's result reaches the function, whether memoized or freshly executed.</summary>
    public virtual Task<object?> WrapStepAsync(FunctionContext context, StepInfo step, Func<Task<object?>> next) => next();

    /// <summary>Wraps only the actual execution of a step handler.</summary>
    public virtual Task<object?> WrapStepHandlerAsync(FunctionContext context, StepInfo step, Func<Task<object?>> next) => next();

    /// <summary>
    /// Wraps sending events. Pass changed events to next to transform them. Context is null outside a run.
    /// </summary>
    public virtual Task<SendEventsResult> WrapSendEventAsync(
        FunctionContext? context,
        IReadOnlyList<InngestEvent> events,
        Func<IReadOnlyList<InngestEvent>, Task<SendEventsResult>> next,
        CancellationToken cancellationToken) => next(events);
}
