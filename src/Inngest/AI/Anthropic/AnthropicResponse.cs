// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Inngest;

/// <summary>A response of Anthropic's Messages API.</summary>
public sealed record AnthropicResponse
{
    [JsonPropertyName("id")]
    public string? Id { get; init; }

    /// <summary>message, or error.</summary>
    [JsonPropertyName("type")]
    public string? Type { get; init; }

    [JsonPropertyName("role")]
    public string? Role { get; init; }

    [JsonPropertyName("model")]
    public string? Model { get; init; }

    [JsonPropertyName("content")]
    public IReadOnlyList<AnthropicContentBlock> Content { get; init; } = [];

    /// <summary>Such as end_turn, max_tokens, stop_sequence or tool_use.</summary>
    [JsonPropertyName("stop_reason")]
    public string? StopReason { get; init; }

    [JsonPropertyName("stop_sequence")]
    public string? StopSequence { get; init; }

    [JsonPropertyName("usage")]
    public AnthropicUsage? Usage { get; init; }

    [JsonPropertyName("error")]
    public AnthropicError? Error { get; init; }

    /// <summary>Properties this record does not have, sent and read as they are.</summary>
    [JsonExtensionData]
    public IDictionary<string, JsonElement>? AdditionalProperties { get; set; }

    /// <summary>The text of the text blocks, joined, or null when there are none.</summary>
    [JsonIgnore]
    public string? Text
    {
        get
        {
            var texts = Content.Where(static block => block.Type == "text" && block.Text is not null).Select(static block => block.Text).ToList();
            return texts.Count == 0 ? null : string.Concat(texts);
        }
    }
}
