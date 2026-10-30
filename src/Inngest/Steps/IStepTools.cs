// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using Inngest.Steps;

namespace Inngest;

/// <summary>
/// Every step id must be unique within a run and stable across replays. Reusing an id in a loop is allowed;
/// later uses get a ":&lt;n&gt;" suffix in the order they are called.
/// </summary>
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

    /// <summary>
    /// Runs the handler once, like RunAsync, as a step that Inngest shows as a call to an AI model, with the input
    /// recorded so its UI can show it. A rerun with an input edited in that UI hands the handler the edited input.
    /// </summary>
    Task<TResult> AIWrapAsync<TInput, TResult>(string id, TInput input, Func<TInput, CancellationToken, Task<TResult>> handler) =>
        throw new NotSupportedException($"{GetType().Name} does not implement AIWrapAsync.");

    /// <summary>
    /// Runs the handler once, like RunAsync, in a service scope of its own that exists only while the handler runs. The
    /// handler resolves what it needs from the provider it is given, so a DbContext or any other scoped service is new
    /// for each step and each attempt, and nothing tracked in one step reaches the next. The result is saved as JSON,
    /// so return plain data, not entities.
    /// </summary>
    Task<T> RunScopedAsync<T>(string id, Func<IServiceProvider, CancellationToken, Task<T>> handler) =>
        throw new NotSupportedException($"{GetType().Name} does not implement RunScopedAsync.");

    /// <summary>
    /// Publishes data to a realtime topic, tagged with the run, and returns it. The step publishes once: a replay
    /// returns the memoized data without publishing again.
    /// </summary>
    Task<T> PublishAsync<T>(string id, RealtimeTopic topic, T data) =>
        throw new NotSupportedException($"{GetType().Name} does not implement PublishAsync.");

    /// <summary>Publishes data to a typed realtime topic once, as PublishAsync does for any topic.</summary>
    Task<T> PublishAsync<T>(string id, RealtimeTopic<T> topic, T data) =>
        PublishAsync(id, (topic ?? throw new ArgumentNullException(nameof(topic))).Untyped, data);

    /// <summary>
    /// Streams text to a realtime topic as the chunks arrive, tagged with the run, and returns all of it. The step streams
    /// once: a replay returns the memoized text without enumerating the chunks, so a model call behind them is not made
    /// again.
    /// </summary>
    Task<string> PublishStreamAsync(string id, RealtimeTopic topic, IAsyncEnumerable<string> chunks) =>
        throw new NotSupportedException($"{GetType().Name} does not implement PublishStreamAsync.");

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

    /// <summary>
    /// Runs the handler once, like RunAsync, as a step that Inngest shows as a call to an AI model, with the input
    /// recorded so its UI can show it. A rerun with an input edited in that UI hands the handler the edited input.
    /// </summary>
    Task<TResult> AIWrapAsync<TInput, TResult>(StepOptions stepOptions, TInput input, Func<TInput, CancellationToken, Task<TResult>> handler) =>
        AIWrapAsync(IdOf(stepOptions), input, handler);

    /// <summary>
    /// Runs the handler once, like RunAsync, in a service scope of its own that exists only while the handler runs. The
    /// handler resolves what it needs from the provider it is given, so a DbContext or any other scoped service is new
    /// for each step and each attempt, and nothing tracked in one step reaches the next. The result is saved as JSON,
    /// so return plain data, not entities.
    /// </summary>
    Task<T> RunScopedAsync<T>(StepOptions stepOptions, Func<IServiceProvider, CancellationToken, Task<T>> handler) =>
        RunScopedAsync(IdOf(stepOptions), handler);

    /// <summary>
    /// Publishes data to a realtime topic, tagged with the run, and returns it. The step publishes once: a replay
    /// returns the memoized data without publishing again.
    /// </summary>
    Task<T> PublishAsync<T>(StepOptions stepOptions, RealtimeTopic topic, T data) => PublishAsync(IdOf(stepOptions), topic, data);

    /// <summary>Publishes data to a typed realtime topic once, as PublishAsync does for any topic.</summary>
    Task<T> PublishAsync<T>(StepOptions stepOptions, RealtimeTopic<T> topic, T data) =>
        PublishAsync(stepOptions, (topic ?? throw new ArgumentNullException(nameof(topic))).Untyped, data);

    /// <summary>
    /// Streams text to a realtime topic as the chunks arrive, tagged with the run, and returns all of it. The step streams
    /// once: a replay returns the memoized text without enumerating the chunks, so a model call behind them is not made
    /// again.
    /// </summary>
    Task<string> PublishStreamAsync(StepOptions stepOptions, RealtimeTopic topic, IAsyncEnumerable<string> chunks) =>
        PublishStreamAsync(IdOf(stepOptions), topic, chunks);

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
