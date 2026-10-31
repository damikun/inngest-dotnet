// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.

using System.Runtime.CompilerServices;
using System.Text.Json;
using Inngest.Execution;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Inngest.Extensions.AI;

// Inside a run, a call to the model is a step: the model is called once, the complete response is memoized, and a replay
// gets it back without calling the model. Everywhere else, outside a run and inside a step's handler, where steps cannot be
// nested, the call goes to the inner client as it is.
//
// The decision is made per call, from the invocation the calling code runs in, so one client serves any number of runs
// and the same pipeline works in a function, a controller and a unit test.
internal sealed class InngestStepsChatClient(IChatClient innerClient, InngestStepsOptions options) : DelegatingChatClient(innerClient)
{
    private const string DefaultStepId = "chat";

    // How long the text of a stream waits to fill a chunk before it is published as it is.
    private static readonly TimeSpan FlushInterval = TimeSpan.FromMilliseconds(100);

    public override Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? chatOptions = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);
        var callOptions = CallSettings.Take(chatOptions, out var settings);

        // The step is registered before this returns, so that repeated calls claim their ids in the order they were made.
        return Invocation.AmbientSteps is { } steps
            ? CallAsStepAsync(steps, messages, callOptions, settings, streaming: false, cancellationToken)
            : InnerClient.GetResponseAsync(messages, callOptions, cancellationToken);
    }

    public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? chatOptions = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);
        var callOptions = CallSettings.Take(chatOptions, out var settings);
        return Invocation.AmbientSteps is null
            ? InnerClient.GetStreamingResponseAsync(messages, callOptions, cancellationToken)
            : StreamAsStepAsync(messages, callOptions, settings, cancellationToken);
    }

    // Like every IChatClient stream, nothing happens until it is enumerated, so a stream nobody reads is not a model call.
    // The step runs the model's whole stream inside its handler, because a step cannot hand out results as they arrive;
    // what the caller gets is the memoized response, as the updates it was made of.
    private async IAsyncEnumerable<ChatResponseUpdate> StreamAsStepAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? callOptions,
        CallSettings settings,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // Enumerated where a step handler runs, the stream is a plain one. It stays in the caller's synchronization context
        // like the stages of Microsoft.Extensions.AI do, which the inner client may need.
        if (Invocation.AmbientSteps is not { } steps)
        {
#pragma warning disable CA2007 // Consider calling ConfigureAwait on the awaited task
            await foreach (var update in InnerClient.GetStreamingResponseAsync(messages, callOptions, cancellationToken))
#pragma warning restore CA2007
            {
                yield return update;
            }

            yield break;
        }

        var response = await CallAsStepAsync(steps, messages, callOptions, settings, streaming: true, cancellationToken).ConfigureAwait(false);
        foreach (var update in response.ToChatResponseUpdates())
        {
            yield return update;
        }
    }

    private async Task<ChatResponse> CallAsStepAsync(
        IStepTools steps,
        IEnumerable<ChatMessage> messages,
        ChatOptions? callOptions,
        CallSettings settings,
        bool streaming,
        CancellationToken cancellationToken)
    {
        // The logger of the function drops what is written while the run replays steps it has already memoized, so a
        // warning about a step is written once per run and not once per call.
        var context = Invocation.AmbientContext!;
        var logger = context.Logger;
        var stepId = settings.StepId ?? DefaultStepId;

        // Copies, because the handler may run after the caller has changed its messages and options for the next call, and
        // what the model is called with has to be what the step recorded.
        List<ChatMessage> history = [.. messages];
        callOptions = callOptions?.Clone();
        var realtime = streaming && settings.StreamTo is not null ? context.Services.GetRequiredService<IInngestRealtime>() : null;
        var time = context.Services.GetService<TimeProvider>() ?? TimeProvider.System;

        // The digest goes with the saved response whether the input is recorded or not.
        var serialized = ChatJson.TryWriteInput(history, callOptions, out var problem);
        if (problem is not null && options.RecordInput)
        {
            Log.InputNotRecorded(logger, stepId, problem);
        }

        var input = options.RecordInput ? serialized?.Json : null;
        var digest = serialized?.Digest;

        // The handler gets the recorded input back, or an edit of it from the Inngest UI, which is then what the model is
        // called with. Without an edit it calls the model with the copies it holds of the caller's messages and options,
        // which carry what JSON would not (raw representations of the provider, additional properties of any type).
        async Task<JsonElement> HandleAsync(JsonElement received, CancellationToken stepToken)
        {
            var (callMessages, optionsOfCall) = input is { } recorded && ChatJson.IsEdited(received, recorded)
                ? ChatJson.ReadInput(received, callOptions)
                : (history, callOptions);

            using var linked = cancellationToken.CanBeCanceled ? CancellationTokenSource.CreateLinkedTokenSource(stepToken, cancellationToken) : null;
            var token = linked?.Token ?? stepToken;
            ChatResponse response;
            if (!streaming)
            {
                response = await InnerClient.GetResponseAsync(callMessages, optionsOfCall, token).ConfigureAwait(false);
            }
            else
            {
                var publisher = realtime is null ? null : new StreamPublisher(realtime, settings.StreamTo!, stepId, logger, time, FlushInterval, token);
                var updates = new List<ChatResponseUpdate>();
                try
                {
                    await foreach (var update in InnerClient.GetStreamingResponseAsync(callMessages, optionsOfCall, token).ConfigureAwait(false))
                    {
                        updates.Add(update);
                        publisher?.Write(update.Text);
                    }
                }
                finally
                {
                    // The stream ends where the model stopped, also when it failed, and the retry of the step streams again, from the
                    // start and as a stream of its own.
                    if (publisher is not null)
                    {
                        await publisher.CompleteAsync().ConfigureAwait(false);
                    }
                }

                response = updates.ToChatResponse();
            }

            var written = ChatJson.WriteResponse(response, digest, out var dropped);
            if (dropped is not null)
            {
                Log.AdditionalPropertiesLeftOut(logger, stepId, dropped);
            }

            return written;
        }

        var json = input is { } stepInput
            ? await steps.AIWrapAsync(stepId, stepInput, HandleAsync).ConfigureAwait(false)
            : await steps.RunAsync(stepId, stepToken => HandleAsync(default, stepToken)).ConfigureAwait(false);
        InputChecks.Check(stepId, ChatJson.InputDigestOf(json), digest);
        return ChatJson.ReadResponse(json);
    }
}
