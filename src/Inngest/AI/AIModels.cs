// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using Inngest.AI;

namespace Inngest;

/// <summary>
/// The models of the providers Inngest can call for an inference step, as @inngest/ai has them: each fills in the URL
/// of the provider's API, its headers, the API key and the defaults of every request. A key left out of the options is
/// read from the provider's environment variable when the model is made, and is empty when that is not set either.
/// </summary>
public static class AIModels
{
    /// <summary>A model of OpenAI's chat completions API, at https://api.openai.com/v1/chat/completions by default.</summary>
    public static InferModel<OpenAIChatRequest, OpenAIChatResponse> OpenAI(OpenAIModelOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return OpenAIChat(AIFormat.OpenAIChat, options, "OPENAI_API_KEY", "https://api.openai.com/v1/");
    }

    /// <summary>A model of the OpenAI Responses API, at https://api.openai.com/v1/responses by default.</summary>
    public static InferModel<OpenAIResponsesRequest, OpenAIResponsesResponse> OpenAIResponses(OpenAIResponsesModelOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrEmpty(options.Model, nameof(options));
        return new(
            AIFormat.OpenAIResponses,
            Endpoint(options.BaseUrl, "https://api.openai.com/v1/", "responses"),
            Key(options.ApiKey, "OPENAI_API_KEY"),
            headers: null,
            options.Model,
            options.DefaultParameters,
            AIJsonContext.Default.OpenAIResponsesRequest,
            AIJsonContext.Default.OpenAIResponsesResponse);
    }

    /// <summary>
    /// A model of Anthropic's Messages API, at https://api.anthropic.com/v1/messages by default, with the
    /// anthropic-version header 2023-06-01.
    /// </summary>
    public static InferModel<AnthropicRequest, AnthropicResponse> Anthropic(AnthropicModelOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrEmpty(options.Model, nameof(options));
        var headers = new Dictionary<string, string>(StringComparer.Ordinal) { ["anthropic-version"] = "2023-06-01" };
        if (options.BetaHeaders is { Count: > 0 } betas)
        {
            headers["anthropic-beta"] = string.Join(',', betas);
        }

        var defaults = options.DefaultParameters ?? new AnthropicRequest();
        return new(
            AIFormat.Anthropic,
            Endpoint(options.BaseUrl, "https://api.anthropic.com/v1/", "messages"),
            Key(options.ApiKey, "ANTHROPIC_API_KEY"),
            headers,
            options.Model,
            defaults with { MaxTokens = defaults.MaxTokens ?? options.MaxTokens },
            AIJsonContext.Default.AnthropicRequest,
            AIJsonContext.Default.AnthropicResponse);
    }

    /// <summary>
    /// A model of the Gemini API's generateContent, at
    /// https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent by default. The key goes in the
    /// URL's key parameter, as the Gemini API takes it.
    /// </summary>
    public static InferModel<GeminiRequest, GeminiResponse> Gemini(GeminiModelOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrEmpty(options.Model, nameof(options));
        var key = Key(options.ApiKey, "GEMINI_API_KEY");
        return new(
            AIFormat.Gemini,
            Endpoint(options.BaseUrl, "https://generativelanguage.googleapis.com/v1beta/", $"models/{options.Model}:generateContent?key={Uri.EscapeDataString(key)}"),
            key,
            headers: null,
            options.Model,
            options.DefaultParameters,
            AIJsonContext.Default.GeminiRequest,
            AIJsonContext.Default.GeminiResponse,
            bodyHasModel: false,
            mergedObjects: ["generationConfig", "thinkingConfig"]);
    }

    /// <summary>A model of xAI's Grok, which speaks the OpenAI chat completions format, at https://api.x.ai/v1/chat/completions by default.</summary>
    public static InferModel<OpenAIChatRequest, OpenAIChatResponse> Grok(OpenAIModelOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return OpenAIChat(AIFormat.Grok, options, "XAI_API_KEY", "https://api.x.ai/v1/");
    }

    /// <summary>
    /// A model of DeepSeek, which speaks the OpenAI chat completions format, at
    /// https://api.deepseek.com/v1/chat/completions by default.
    /// </summary>
    public static InferModel<OpenAIChatRequest, OpenAIChatResponse> DeepSeek(OpenAIModelOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return OpenAIChat(AIFormat.OpenAIChat, options, "DEEPSEEK_API_KEY", "https://api.deepseek.com/v1/");
    }

    /// <summary>
    /// A deployment of Azure OpenAI, at {endpoint}/openai/deployments/{deployment}/chat/completions?api-version={version},
    /// with the key in the api-key header.
    /// </summary>
    public static InferModel<OpenAIChatRequest, OpenAIChatResponse> AzureOpenAI(AzureOpenAIModelOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.Endpoint, nameof(options));
        ArgumentException.ThrowIfNullOrEmpty(options.Deployment, nameof(options));
        ArgumentException.ThrowIfNullOrEmpty(options.ApiVersion, nameof(options));
        var key = Key(options.ApiKey, "AZURE_OPENAI_API_KEY");
        var path = $"openai/deployments/{Uri.EscapeDataString(options.Deployment)}/chat/completions?api-version={Uri.EscapeDataString(options.ApiVersion)}";
        return new(
            AIFormat.AzureOpenAI,
            Endpoint(options.Endpoint, null, path),
            key,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["api-key"] = key, ["Content-Type"] = "application/json" },
            string.IsNullOrEmpty(options.Model) ? null : options.Model,
            options.DefaultParameters,
            AIJsonContext.Default.OpenAIChatRequest,
            AIJsonContext.Default.OpenAIChatResponse);
    }

    private static InferModel<OpenAIChatRequest, OpenAIChatResponse> OpenAIChat(AIFormat format, OpenAIModelOptions options, string keyVariable, string baseUrl)
    {
        ArgumentException.ThrowIfNullOrEmpty(options.Model, nameof(options));
        return new(
            format,
            Endpoint(options.BaseUrl, baseUrl, "chat/completions"),
            Key(options.ApiKey, keyVariable),
            headers: null,
            options.Model,
            options.DefaultParameters,
            AIJsonContext.Default.OpenAIChatRequest,
            AIJsonContext.Default.OpenAIChatResponse);
    }

    private static string Key(string? apiKey, string variable) =>
        string.IsNullOrEmpty(apiKey) ? Environment.GetEnvironmentVariable(variable) ?? string.Empty : apiKey;

    // The path is added to the base URL, so the base gets a trailing slash: without one, its last segment would be
    // replaced instead.
    private static Uri Endpoint(Uri? baseUrl, string? defaultBaseUrl, string path)
    {
        var root = baseUrl ?? new Uri(defaultBaseUrl!);
        if (!root.IsAbsoluteUri || root.Scheme is not ("http" or "https"))
        {
            throw new ArgumentException($"The base URL of a model must be an absolute http or https URL, not {root}.", nameof(baseUrl));
        }

        if (!root.AbsolutePath.EndsWith('/'))
        {
            root = new UriBuilder(root) { Path = root.AbsolutePath + "/" }.Uri;
        }

        return new Uri(root, path);
    }
}
