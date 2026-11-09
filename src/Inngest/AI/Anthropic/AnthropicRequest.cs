// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Inngest;

/// <summary>
/// The body of a request to Anthropic's Messages API. A model of AIModels fills in Model and MaxTokens when they are
/// null, and its default parameters where the request has none.
/// </summary>
public sealed record AnthropicRequest
{
    [JsonPropertyName("model")]
    public string? Model { get; init; }

    [JsonPropertyName("max_tokens")]
    public int? MaxTokens { get; init; }

    [JsonPropertyName("messages")]
    public IReadOnlyList<AnthropicMessage> Messages { get; init; } = [];

    /// <summary>The system prompt.</summary>
    [JsonPropertyName("system")]
    public string? System { get; init; }

    [JsonPropertyName("temperature")]
    public double? Temperature { get; init; }

    [JsonPropertyName("top_p")]
    public double? TopP { get; init; }

    [JsonPropertyName("top_k")]
    public int? TopK { get; init; }

    [JsonPropertyName("stop_sequences")]
    public IReadOnlyList<string>? StopSequences { get; init; }

    [JsonPropertyName("tools")]
    public IReadOnlyList<AnthropicTool>? Tools { get; init; }

    [JsonPropertyName("tool_choice")]
    public AnthropicToolChoice? ToolChoice { get; init; }

    /// <summary>Extended thinking, such as {"type": "enabled", "budget_tokens": 2048}.</summary>
    [JsonPropertyName("thinking")]
    public JsonElement? Thinking { get; init; }

    [JsonPropertyName("metadata")]
    public JsonElement? Metadata { get; init; }

    /// <summary>Properties this record does not have, sent and read as they are.</summary>
    [JsonExtensionData]
    public IDictionary<string, JsonElement>? AdditionalProperties { get; set; }
}
