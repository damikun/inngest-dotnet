// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Inngest;

/// <summary>
/// The body of a request in the OpenAI chat completions format, which OpenAI, Azure OpenAI, Grok and DeepSeek take. A
/// model of AIModels fills in Model when it is null, and its default parameters where the request has none.
/// </summary>
public sealed record OpenAIChatRequest
{
    [JsonPropertyName("model")]
    public string? Model { get; init; }

    [JsonPropertyName("messages")]
    public IReadOnlyList<OpenAIChatMessage> Messages { get; init; } = [];

    [JsonPropertyName("temperature")]
    public double? Temperature { get; init; }

    [JsonPropertyName("top_p")]
    public double? TopP { get; init; }

    /// <summary>The limit on the tokens of the answer that current OpenAI models take.</summary>
    [JsonPropertyName("max_completion_tokens")]
    public int? MaxCompletionTokens { get; init; }

    /// <summary>The older limit, which OpenAI's reasoning models refuse and other providers of the format still take.</summary>
    [JsonPropertyName("max_tokens")]
    public int? MaxTokens { get; init; }

    /// <summary>How many choices to generate.</summary>
    [JsonPropertyName("n")]
    public int? N { get; init; }

    [JsonPropertyName("stop")]
    public IReadOnlyList<string>? Stop { get; init; }

    [JsonPropertyName("presence_penalty")]
    public double? PresencePenalty { get; init; }

    [JsonPropertyName("frequency_penalty")]
    public double? FrequencyPenalty { get; init; }

    [JsonPropertyName("seed")]
    public long? Seed { get; init; }

    [JsonPropertyName("user")]
    public string? User { get; init; }

    [JsonPropertyName("tools")]
    public IReadOnlyList<OpenAIChatTool>? Tools { get; init; }

    /// <summary>"auto", "none", "required", or an object that names the function to call.</summary>
    [JsonPropertyName("tool_choice")]
    public JsonElement? ToolChoice { get; init; }

    [JsonPropertyName("parallel_tool_calls")]
    public bool? ParallelToolCalls { get; init; }

    /// <summary>Such as {"type": "json_schema", "json_schema": {...}} for structured output.</summary>
    [JsonPropertyName("response_format")]
    public JsonElement? ResponseFormat { get; init; }

    [JsonPropertyName("reasoning_effort")]
    public string? ReasoningEffort { get; init; }

    /// <summary>Properties this record does not have, sent and read as they are.</summary>
    [JsonExtensionData]
    public IDictionary<string, JsonElement>? AdditionalProperties { get; set; }
}
