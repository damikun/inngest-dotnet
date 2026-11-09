// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Inngest;

/// <summary>
/// A block of content in Anthropic's Messages API: text, an image, a tool use, a tool result, thinking and so on. Which
/// properties are set depends on the type.
/// </summary>
/// <param name="Type">text, image, document, tool_use, tool_result, thinking and so on.</param>
public sealed record AnthropicContentBlock(
    [property: JsonPropertyName("type")] string Type)
{
    [JsonPropertyName("text")]
    public string? Text { get; init; }

    /// <summary>The id of a tool use.</summary>
    [JsonPropertyName("id")]
    public string? Id { get; init; }

    /// <summary>The tool a tool use calls.</summary>
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    /// <summary>The arguments of a tool use.</summary>
    [JsonPropertyName("input")]
    public JsonElement? Input { get; init; }

    /// <summary>The tool use a tool result answers.</summary>
    [JsonPropertyName("tool_use_id")]
    public string? ToolUseId { get; init; }

    /// <summary>The content of a tool result: a string or an array of blocks.</summary>
    [JsonPropertyName("content")]
    public JsonElement? Content { get; init; }

    [JsonPropertyName("is_error")]
    public bool? IsError { get; init; }

    /// <summary>The source of an image or a document.</summary>
    [JsonPropertyName("source")]
    public JsonElement? Source { get; init; }

    [JsonPropertyName("thinking")]
    public string? Thinking { get; init; }

    [JsonPropertyName("signature")]
    public string? Signature { get; init; }

    /// <summary>Properties this record does not have, sent and read as they are.</summary>
    [JsonExtensionData]
    public IDictionary<string, JsonElement>? AdditionalProperties { get; set; }

    /// <summary>A text block.</summary>
    public static AnthropicContentBlock FromText(string text) => new("text") { Text = text };

    /// <summary>The result of a tool use, for the model's next turn.</summary>
    public static AnthropicContentBlock ToolResult(string toolUseId, string content, bool isError = false) =>
        new("tool_result")
        {
            ToolUseId = toolUseId,
            Content = JsonSerializer.SerializeToElement(content, Inngest.AI.AIJsonContext.Default.String),
            IsError = isError ? true : null,
        };
}
