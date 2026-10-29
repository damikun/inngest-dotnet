// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using Inngest.Execution;
using Inngest.Internal;

namespace Inngest.Steps;

// The step tools of one invocation. Arguments are checked here, on the caller's thread; everything else happens
// on the invocation's scheduler. Once the invocation has answered, new steps are ignored and never complete.
internal sealed class StepTools(Invocation invocation) : IStepTools
{
    public Task<T> RunAsync<T>(string id, Func<CancellationToken, Task<T>> handler) => Run(id, null, handler);

    public Task SleepAsync(string id, TimeSpan duration) => Sleep(id, null, duration);

    public Task SleepUntilAsync(string id, DateTimeOffset until) => SleepUntil(id, null, until);

    public Task<InngestEvent<TData>?> WaitForEventAsync<TData>(string id, WaitForEventOptions options) => WaitForEvent<TData>(id, null, options);

    public Task<TResult> InvokeAsync<TResult>(string id, InvokeOptions options) => Invoke<TResult>(id, null, options);

    public Task<SendEventsResult> SendEventAsync(string id, IReadOnlyList<InngestEvent> events) => SendEvent(id, null, events);

    public Task<T> RunAsync<T>(StepOptions stepOptions, Func<CancellationToken, Task<T>> handler) =>
        Run(IdOf(stepOptions), stepOptions, handler);

    public Task SleepAsync(StepOptions stepOptions, TimeSpan duration) => Sleep(IdOf(stepOptions), stepOptions, duration);

    public Task SleepUntilAsync(StepOptions stepOptions, DateTimeOffset until) => SleepUntil(IdOf(stepOptions), stepOptions, until);

    public Task<InngestEvent<TData>?> WaitForEventAsync<TData>(StepOptions stepOptions, WaitForEventOptions options) =>
        WaitForEvent<TData>(IdOf(stepOptions), stepOptions, options);

    public Task<TResult> InvokeAsync<TResult>(StepOptions stepOptions, InvokeOptions options) =>
        Invoke<TResult>(IdOf(stepOptions), stepOptions, options);

    public Task<SendEventsResult> SendEventAsync(StepOptions stepOptions, IReadOnlyList<InngestEvent> events) =>
        SendEvent(IdOf(stepOptions), stepOptions, events);

    public Task<InngestSignal<TData>?> WaitForSignalAsync<TData>(string id, WaitForSignalOptions options) =>
        WaitForSignal<TData>(id, null, options);

    public Task<string?> SendSignalAsync(string id, InngestSignal signal) => SendSignal(id, null, signal);

    public Task<HttpResponseMessage> FetchAsync(string id, HttpRequestMessage request) => Fetch(id, null, request);

    public Task<TResponse> InferAsync<TResponse>(string id, InferOptions options) => Infer<TResponse>(id, null, options);

    public Task<InngestSignal<TData>?> WaitForSignalAsync<TData>(StepOptions stepOptions, WaitForSignalOptions options) =>
        WaitForSignal<TData>(IdOf(stepOptions), stepOptions, options);

    public Task<string?> SendSignalAsync(StepOptions stepOptions, InngestSignal signal) => SendSignal(IdOf(stepOptions), stepOptions, signal);

    public Task<HttpResponseMessage> FetchAsync(StepOptions stepOptions, HttpRequestMessage request) =>
        Fetch(IdOf(stepOptions), stepOptions, request);

    public Task<TResponse> InferAsync<TResponse>(StepOptions stepOptions, InferOptions options) =>
        Infer<TResponse>(IdOf(stepOptions), stepOptions, options);

    private Task<T> Run<T>(string id, StepOptions? stepOptions, Func<CancellationToken, Task<T>> handler)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        ArgumentNullException.ThrowIfNull(handler);
        return Register(new RunStep<T>(id, handler), stepOptions);
    }

    private Task Sleep(string id, StepOptions? stepOptions, TimeSpan duration)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        ArgumentOutOfRangeException.ThrowIfLessThan(duration, TimeSpan.Zero);
        return Register(new SleepStep(id, duration), stepOptions);
    }

    private Task SleepUntil(string id, StepOptions? stepOptions, DateTimeOffset until)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        return Register(new SleepStep(id, until), stepOptions);
    }

    private Task<InngestEvent<TData>?> WaitForEvent<TData>(string id, StepOptions? stepOptions, WaitForEventOptions options)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrEmpty(options.Event);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.Timeout, TimeSpan.Zero);
        return Register(new WaitForEventStep<TData>(id, options), stepOptions);
    }

    private Task<TResult> Invoke<TResult>(string id, StepOptions? stepOptions, InvokeOptions options)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrEmpty(options.FunctionId);
        JsonPayload.EnsureObject(options.Data, invocation.SerializerOptions, "data", "invoke step", id);
        JsonPayload.EnsureObject(options.User, invocation.SerializerOptions, "user", "invoke step", id);
        if (options.Timeout is { } timeout)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(timeout, TimeSpan.Zero);
        }

        return Register(new InvokeStep<TResult>(id, options), stepOptions);
    }

    private Task<SendEventsResult> SendEvent(string id, StepOptions? stepOptions, IReadOnlyList<InngestEvent> events)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        ArgumentNullException.ThrowIfNull(events);
        return Register(new SendEventStep(id, events), stepOptions);
    }

    private Task<InngestSignal<TData>?> WaitForSignal<TData>(string id, StepOptions? stepOptions, WaitForSignalOptions options)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrEmpty(options.Signal);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.Timeout, TimeSpan.Zero);
        return Register(new WaitForSignalStep<TData>(id, options), stepOptions);
    }

    private Task<string?> SendSignal(string id, StepOptions? stepOptions, InngestSignal signal)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        ArgumentNullException.ThrowIfNull(signal);
        ArgumentException.ThrowIfNullOrEmpty(signal.Name);
        return Register(new SendSignalStep(id, signal), stepOptions);
    }

    private Task<HttpResponseMessage> Fetch(string id, StepOptions? stepOptions, HttpRequestMessage request)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        ArgumentNullException.ThrowIfNull(request);
        if (request.RequestUri is not { IsAbsoluteUri: true })
        {
            throw new ArgumentException($"The request of fetch step \"{id}\" needs an absolute URI.", nameof(request));
        }

        var headers = FetchStep.ReadHeaders(request);
        return request.Content is null
            ? Register(new FetchStep(id, request, headers, null), stepOptions)
            : FetchWithBodyAsync(id, stepOptions, request, headers);
    }

    // Inngest takes the body as text. It is read before the step is registered, which for the usual buffered
    // content happens before this method returns, so repeated ids are still claimed in call order.
    private async Task<HttpResponseMessage> FetchWithBodyAsync(
        string id,
        StepOptions? stepOptions,
        HttpRequestMessage request,
        KeyValuePair<string, string>[] headers)
    {
        var body = await request.Content!.ReadAsStringAsync().ConfigureAwait(false);
        return await Register(new FetchStep(id, request, headers, body), stepOptions).ConfigureAwait(false);
    }

    private Task<TResponse> Infer<TResponse>(string id, StepOptions? stepOptions, InferOptions options)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.Url);
        ArgumentNullException.ThrowIfNull(options.Body);
        if (!options.Url.IsAbsoluteUri)
        {
            throw new ArgumentException($"The URL of infer step \"{id}\" must be absolute.", nameof(options));
        }

        if (!Enum.IsDefined(options.Format))
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.Format, $"Infer step \"{id}\" has an unknown format.");
        }

        JsonPayload.EnsureObject(options.Body, invocation.SerializerOptions, "body", "infer step", id);
        return Register(new InferStep<TResponse>(id, options), stepOptions);
    }

    // The options have to be on the step before it is registered, which may hand it to middleware right away.
    private Task<T> Register<T>(FoundStep<T> step, StepOptions? stepOptions)
    {
        if (stepOptions is not null)
        {
            step.Apply(stepOptions);
        }

        invocation.Register(step);
        return step.Completion;
    }

    private static string IdOf(StepOptions stepOptions)
    {
        ArgumentNullException.ThrowIfNull(stepOptions);
        return stepOptions.Id;
    }
}
