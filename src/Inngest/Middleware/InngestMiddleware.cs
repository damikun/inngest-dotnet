// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

namespace Inngest;

// Hooks into function runs, steps and event sending. Override only what you need; every hook defaults to doing
// nothing. Middleware runs in registration order, so the first registered wrapper is the outermost one.
public abstract class InngestMiddleware
{
    public virtual ValueTask OnRunStartAsync(FunctionContext context) => ValueTask.CompletedTask;

    // Called once all memoized steps have been replayed and the run reaches new code.
    public virtual ValueTask OnMemoizationEndAsync(FunctionContext context) => ValueTask.CompletedTask;

    public virtual ValueTask OnRunCompleteAsync(FunctionContext context, object? output) => ValueTask.CompletedTask;

    public virtual ValueTask OnRunErrorAsync(FunctionContext context, Exception exception, bool isFinalAttempt) =>
        ValueTask.CompletedTask;

    // Called before a step handler executes. Memoized steps do not execute and do not raise step events.
    public virtual ValueTask OnStepStartAsync(FunctionContext context, StepInfo step) => ValueTask.CompletedTask;

    public virtual ValueTask OnStepCompleteAsync(FunctionContext context, StepInfo step, object? output) =>
        ValueTask.CompletedTask;

    public virtual ValueTask OnStepErrorAsync(FunctionContext context, StepInfo step, Exception exception, bool isFinalAttempt) =>
        ValueTask.CompletedTask;

    // Wraps the function body on every invocation, including replays.
    public virtual Task<object?> WrapFunctionAsync(FunctionContext context, Func<Task<object?>> next) => next();

    // Wraps how a step's result reaches the function, whether memoized or freshly executed.
    public virtual Task<object?> WrapStepAsync(FunctionContext context, StepInfo step, Func<Task<object?>> next) => next();

    // Wraps only the actual execution of a step handler.
    public virtual Task<object?> WrapStepHandlerAsync(FunctionContext context, StepInfo step, Func<Task<object?>> next) => next();

    // Wraps sending events. Pass changed events to next to transform them. Context is null outside a run.
    public virtual Task<SendEventsResult> WrapSendEventAsync(
        FunctionContext? context,
        IReadOnlyList<InngestEvent> events,
        Func<IReadOnlyList<InngestEvent>, Task<SendEventsResult>> next,
        CancellationToken cancellationToken) => next(events);
}
