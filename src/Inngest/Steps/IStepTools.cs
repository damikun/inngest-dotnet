// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using Inngest.Steps;

namespace Inngest;

// Every step id must be unique within a run and stable across replays. Reusing an id in a loop is allowed;
// later uses get a ":<n>" suffix in the order they are called.
public interface IStepTools
{
    /// <summary>Runs the handler once, retrying it on failure, and returns its memoized result on every replay.</summary>
    Task<T> RunAsync<T>(string id, Func<CancellationToken, Task<T>> handler);

    /// <summary>Pauses the run for the given duration.</summary>
    Task SleepAsync(string id, TimeSpan duration);

    /// <summary>Pauses the run until the given time.</summary>
    Task SleepUntilAsync(string id, DateTimeOffset until);

    /// <summary>Waits for a matching event and returns it, or null when the timeout passes first.</summary>
    Task<InngestEvent<TData>?> WaitForEventAsync<TData>(string id, WaitForEventOptions options);

    /// <summary>Runs another Inngest function and returns its result.</summary>
    Task<TResult> InvokeAsync<TResult>(string id, InvokeOptions options);

    /// <summary>Sends events from inside the run so that a replay never sends them twice.</summary>
    Task<SendEventsResult> SendEventAsync(string id, IReadOnlyList<InngestEvent> events);

    /// <summary>Waits for a signal sent to this run and returns it, or null when the timeout passes first.</summary>
    Task<InngestSignal<TData>?> WaitForSignalAsync<TData>(string id, WaitForSignalOptions options) =>
        throw new NotSupportedException($"{GetType().Name} does not implement WaitForSignalAsync.");

    /// <summary>Sends a signal from inside the run and returns the id of the run it resumed, or null when no run waits for it.</summary>
    Task<string?> SendSignalAsync(string id, InngestSignal signal) =>
        throw new NotSupportedException($"{GetType().Name} does not implement SendSignalAsync.");

    /// <summary>Has Inngest make the HTTP request, so no worker waits for it, and returns the memoized response.</summary>
    Task<HttpResponseMessage> FetchAsync(string id, HttpRequestMessage request) =>
        throw new NotSupportedException($"{GetType().Name} does not implement FetchAsync.");

    /// <summary>Has Inngest send the request to an AI provider and returns the provider's memoized response.</summary>
    Task<TResponse> InferAsync<TResponse>(string id, InferOptions options) =>
        throw new NotSupportedException($"{GetType().Name} does not implement InferAsync.");

    /// <summary>Runs the function of this app registered as TFunction and returns its result, which must be a TResult.</summary>
    Task<TResult> InvokeAsync<TFunction, TResult>(string id, object? data = null, TimeSpan? timeout = null)
        where TFunction : InngestFunction, IReturns<TResult> =>
        throw new NotSupportedException($"{GetType().Name} does not implement InvokeAsync by function class.");

    // The StepOptions overloads give a step a display name or a parallel mode of its own. They fall back to the
    // members above with the id alone, so a fake that implements only those still works.

    /// <summary>Runs the handler once, retrying it on failure, and returns its memoized result on every replay.</summary>
    Task<T> RunAsync<T>(StepOptions stepOptions, Func<CancellationToken, Task<T>> handler) => RunAsync(IdOf(stepOptions), handler);

    /// <summary>Pauses the run for the given duration.</summary>
    Task SleepAsync(StepOptions stepOptions, TimeSpan duration) => SleepAsync(IdOf(stepOptions), duration);

    /// <summary>Pauses the run until the given time.</summary>
    Task SleepUntilAsync(StepOptions stepOptions, DateTimeOffset until) => SleepUntilAsync(IdOf(stepOptions), until);

    /// <summary>Waits for a matching event and returns it, or null when the timeout passes first.</summary>
    Task<InngestEvent<TData>?> WaitForEventAsync<TData>(StepOptions stepOptions, WaitForEventOptions options) =>
        WaitForEventAsync<TData>(IdOf(stepOptions), options);

    /// <summary>Runs another Inngest function and returns its result.</summary>
    Task<TResult> InvokeAsync<TResult>(StepOptions stepOptions, InvokeOptions options) => InvokeAsync<TResult>(IdOf(stepOptions), options);

    /// <summary>Sends events from inside the run so that a replay never sends them twice.</summary>
    Task<SendEventsResult> SendEventAsync(StepOptions stepOptions, IReadOnlyList<InngestEvent> events) =>
        SendEventAsync(IdOf(stepOptions), events);

    /// <summary>Waits for a signal sent to this run and returns it, or null when the timeout passes first.</summary>
    Task<InngestSignal<TData>?> WaitForSignalAsync<TData>(StepOptions stepOptions, WaitForSignalOptions options) =>
        WaitForSignalAsync<TData>(IdOf(stepOptions), options);

    /// <summary>Sends a signal from inside the run and returns the id of the run it resumed, or null when no run waits for it.</summary>
    Task<string?> SendSignalAsync(StepOptions stepOptions, InngestSignal signal) => SendSignalAsync(IdOf(stepOptions), signal);

    /// <summary>Has Inngest make the HTTP request, so no worker waits for it, and returns the memoized response.</summary>
    Task<HttpResponseMessage> FetchAsync(StepOptions stepOptions, HttpRequestMessage request) => FetchAsync(IdOf(stepOptions), request);

    /// <summary>Has Inngest send the request to an AI provider and returns the provider's memoized response.</summary>
    Task<TResponse> InferAsync<TResponse>(StepOptions stepOptions, InferOptions options) => InferAsync<TResponse>(IdOf(stepOptions), options);

    /// <summary>Runs the function of this app registered as TFunction and returns its result, which must be a TResult.</summary>
    Task<TResult> InvokeAsync<TFunction, TResult>(StepOptions stepOptions, object? data = null, TimeSpan? timeout = null)
        where TFunction : InngestFunction, IReturns<TResult> =>
        InvokeAsync<TFunction, TResult>(IdOf(stepOptions), data, timeout);

    /// <summary>Runs the callback with the parallel mode applied to every step it creates, such as the steps of a race.</summary>
    Task<T> ParallelAsync<T>(ParallelMode mode, Func<Task<T>> steps) => ParallelScope.Run(mode, steps);

    private static string IdOf(StepOptions stepOptions)
    {
        ArgumentNullException.ThrowIfNull(stepOptions);
        return stepOptions.Id;
    }
}
