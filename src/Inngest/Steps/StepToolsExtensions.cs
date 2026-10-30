// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using System.Text.Json;

namespace Inngest;

public static class StepToolsExtensions
{
    public static Task RunAsync(this IStepTools step, string id, Func<CancellationToken, Task> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return step.RunAsync<object?>(id, async cancellationToken =>
        {
            await handler(cancellationToken).ConfigureAwait(false);
            return null;
        });
    }

    public static Task<T> RunAsync<T>(this IStepTools step, string id, Func<Task<T>> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return step.RunAsync(id, _ => handler());
    }

    public static Task RunAsync(this IStepTools step, string id, Func<Task> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return step.RunAsync(id, _ => handler());
    }

    public static Task<T> RunAsync<T>(this IStepTools step, string id, Func<T> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        ThrowIfValueTask(id, handler);
        return step.RunAsync(id, _ => Task.FromResult(handler()));
    }

    public static Task RunAsync(this IStepTools step, string id, Action handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return step.RunAsync<object?>(id, _ =>
        {
            handler();
            return Task.FromResult<object?>(null);
        });
    }

    public static Task<InngestEvent<JsonElement>?> WaitForEventAsync(this IStepTools step, string id, WaitForEventOptions options) =>
        step.WaitForEventAsync<JsonElement>(id, options);

    public static Task<SendEventsResult> SendEventAsync(this IStepTools step, string id, InngestEvent @event) =>
        step.SendEventAsync(id, [@event]);

    /// <summary>Waits for an event of type T. Match and If work as on WaitForEventOptions.</summary>
    public static Task<InngestEvent<T>?> WaitForEventAsync<T>(
        this IStepTools step,
        string id,
        TimeSpan timeout,
        string? match = null,
        string? @if = null)
        where T : IInngestEventData =>
        step.WaitForEventAsync<T>(id, new WaitForEventOptions(T.EventName, timeout) { Match = match, If = @if });

    public static Task<SendEventsResult> SendEventAsync<T>(this IStepTools step, string id, T data)
        where T : IInngestEventData =>
        step.SendEventAsync(id, [InngestEvent.From(data)]);

    public static Task<SendEventsResult> SendEventAsync<T>(this IStepTools step, string id, IEnumerable<T> data)
        where T : IInngestEventData
    {
        ArgumentNullException.ThrowIfNull(data);
        return step.SendEventAsync(id, [.. data.Select(InngestEvent.From)]);
    }

    public static Task<InngestSignal<JsonElement>?> WaitForSignalAsync(this IStepTools step, string id, WaitForSignalOptions options) =>
        step.WaitForSignalAsync<JsonElement>(id, options);

    public static Task<JsonElement> InferAsync(this IStepTools step, string id, InferOptions options) =>
        step.InferAsync<JsonElement>(id, options);

    public static Task RunAsync(this IStepTools step, StepOptions stepOptions, Func<CancellationToken, Task> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return step.RunAsync<object?>(stepOptions, async cancellationToken =>
        {
            await handler(cancellationToken).ConfigureAwait(false);
            return null;
        });
    }

    public static Task<T> RunAsync<T>(this IStepTools step, StepOptions stepOptions, Func<Task<T>> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return step.RunAsync(stepOptions, _ => handler());
    }

    public static Task RunAsync(this IStepTools step, StepOptions stepOptions, Func<Task> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return step.RunAsync(stepOptions, _ => handler());
    }

    public static Task<T> RunAsync<T>(this IStepTools step, StepOptions stepOptions, Func<T> handler)
    {
        ArgumentNullException.ThrowIfNull(stepOptions);
        ArgumentNullException.ThrowIfNull(handler);
        ThrowIfValueTask(stepOptions.Id, handler);
        return step.RunAsync(stepOptions, _ => Task.FromResult(handler()));
    }

    public static Task RunAsync(this IStepTools step, StepOptions stepOptions, Action handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return step.RunAsync<object?>(stepOptions, _ =>
        {
            handler();
            return Task.FromResult<object?>(null);
        });
    }

    public static Task<InngestEvent<JsonElement>?> WaitForEventAsync(this IStepTools step, StepOptions stepOptions, WaitForEventOptions options) =>
        step.WaitForEventAsync<JsonElement>(stepOptions, options);

    public static Task<SendEventsResult> SendEventAsync(this IStepTools step, StepOptions stepOptions, InngestEvent @event) =>
        step.SendEventAsync(stepOptions, [@event]);

    public static Task<InngestEvent<T>?> WaitForEventAsync<T>(
        this IStepTools step,
        StepOptions stepOptions,
        TimeSpan timeout,
        string? match = null,
        string? @if = null)
        where T : IInngestEventData =>
        step.WaitForEventAsync<T>(stepOptions, new WaitForEventOptions(T.EventName, timeout) { Match = match, If = @if });

    public static Task<SendEventsResult> SendEventAsync<T>(this IStepTools step, StepOptions stepOptions, T data)
        where T : IInngestEventData =>
        step.SendEventAsync(stepOptions, [InngestEvent.From(data)]);

    public static Task<SendEventsResult> SendEventAsync<T>(this IStepTools step, StepOptions stepOptions, IEnumerable<T> data)
        where T : IInngestEventData
    {
        ArgumentNullException.ThrowIfNull(data);
        return step.SendEventAsync(stepOptions, [.. data.Select(InngestEvent.From)]);
    }

    public static Task<InngestSignal<JsonElement>?> WaitForSignalAsync(this IStepTools step, StepOptions stepOptions, WaitForSignalOptions options) =>
        step.WaitForSignalAsync<JsonElement>(stepOptions, options);

    public static Task<JsonElement> InferAsync(this IStepTools step, StepOptions stepOptions, InferOptions options) =>
        step.InferAsync<JsonElement>(stepOptions, options);

    public static Task ParallelAsync(this IStepTools step, ParallelMode mode, Func<Task> steps)
    {
        ArgumentNullException.ThrowIfNull(steps);
        return step.ParallelAsync<object?>(mode, async () =>
        {
            await steps().ConfigureAwait(false);
            return null;
        });
    }

    // A lambda that returns a ValueTask binds to the Func<T> overloads, and its result would be saved without the work
    // being awaited.
    private static void ThrowIfValueTask<T>(string id, Func<T> handler)
    {
        if (typeof(T) == typeof(ValueTask) || (typeof(T).IsGenericType && typeof(T).GetGenericTypeDefinition() == typeof(ValueTask<>)))
        {
            throw new ArgumentException(
                $"The handler of step \"{id}\" returns a {typeof(T).Name}. Make it an async lambda or call AsTask() so the step waits for it.",
                nameof(handler));
        }
    }
}
