// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.

using System.Runtime.CompilerServices;
using Inngest.Execution;
using Inngest.Extensions.AI;
using Microsoft.Extensions.AI;

namespace Inngest;

/// <summary>
/// An <see cref="IChatClient"/> whose calls Inngest makes: each is an inference step, so Inngest sends the request to a
/// provider that speaks the OpenAI chat completions API and no worker waits for the answer. The step saves the provider's
/// response with the run, so a replay never calls the model again. It works inside a run only, as inference steps do, and
/// throws elsewhere. Inngest stores the request with the run, the API key included. A streaming call returns the saved
/// response as updates once the model has finished. An answer that is an error or has no choices fails the call for good.
/// A "strict" additional property of the options or of a function asks for strict structured outputs. Put it under
/// <c>UseInngestFunctionInvocation</c> to let the model call tools; it needs no <c>UseInngestSteps</c>, being a step already.
/// </summary>
public sealed class InngestInferChatClient : IChatClient
{
    private readonly Uri _endpoint;
    private readonly string _model;
    private readonly string? _apiKey;
    private readonly IReadOnlyDictionary<string, string>? _headers;

    /// <summary>Creates a client for a provider.</summary>
    /// <param name="endpoint">The URL of the chat completions endpoint, such as https://api.openai.com/v1/chat/completions.</param>
    /// <param name="model">The model to ask, unless a call names another in <see cref="ChatOptions.ModelId"/>.</param>
    /// <param name="apiKey">Sent to the provider as a bearer token.</param>
    /// <param name="headers">Sent to the provider as they are.</param>
    public InngestInferChatClient(Uri endpoint, string model, string? apiKey = null, IReadOnlyDictionary<string, string>? headers = null)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentException.ThrowIfNullOrEmpty(model);
        _endpoint = endpoint;
        _model = model;
        _apiKey = apiKey;
        _headers = headers;
    }

    /// <inheritdoc />
    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);
        var callOptions = CallSettings.Take(options, out var settings);
        var steps = Invocation.AmbientSteps
            ?? throw new InvalidOperationException("An inference step can only be made by the code of a run, not outside one or inside the handler of a step.");

        // The step is registered before this returns, so that repeated calls claim their ids in the order they were made.
        return InferAsync(steps, settings.StepId ?? "chat", messages, callOptions);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
        foreach (var update in response.ToChatResponseUpdates())
        {
            yield return update;
        }
    }

    /// <inheritdoc />
    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);
        if (serviceKey is not null)
        {
            return null;
        }

        return serviceType == typeof(ChatClientMetadata)
            ? new ChatClientMetadata("inngest-infer", _endpoint, _model)
            : serviceType.IsInstanceOfType(this) ? this : null;
    }

    /// <inheritdoc />
    public void Dispose()
    {
    }

    private async Task<ChatResponse> InferAsync(IStepTools steps, string stepId, IEnumerable<ChatMessage> messages, ChatOptions? options)
    {
        var body = OpenAIChatJson.WriteRequest(_model, messages, options);
        var inference = new InferOptions(AIFormat.OpenAIChat, _endpoint, body) { AuthKey = _apiKey, Headers = _headers };
        return OpenAIChatJson.ReadResponse(await steps.InferAsync(stepId, inference).ConfigureAwait(false));
    }
}
