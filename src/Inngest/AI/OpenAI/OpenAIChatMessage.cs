// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Inngest;

/// <summary>A message of the OpenAI chat completions format, in a request or in a choice of the response.</summary>
/// <param name="Role">system, user, assistant or tool.</param>
/// <param name="Content">The text of the message.</param>
public sealed record OpenAIChatMessage(
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("content")] string? Content)
{
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    /// <summary>The functions an assistant message asks for.</summary>
    [JsonPropertyName("tool_calls")]
    public IReadOnlyList<OpenAIToolCall>? ToolCalls { get; init; }

    /// <summary>The call a tool message answers.</summary>
    [JsonPropertyName("tool_call_id")]
    public string? ToolCallId { get; init; }

    [JsonPropertyName("refusal")]
    public string? Refusal { get; init; }

    /// <summary>The reasoning that DeepSeek's reasoning model answers with.</summary>
    [JsonPropertyName("reasoning_content")]
    public string? ReasoningContent { get; init; }

    /// <summary>Properties this record does not have, sent and read as they are.</summary>
    [JsonExtensionData]
    public IDictionary<string, JsonElement>? AdditionalProperties { get; set; }

    /// <summary>A system message.</summary>
    public static OpenAIChatMessage System(string content) => new("system", content);

    /// <summary>A user message.</summary>
    public static OpenAIChatMessage User(string content) => new("user", content);

    /// <summary>An assistant message.</summary>
    public static OpenAIChatMessage Assistant(string content) => new("assistant", content);

    /// <summary>The result of a tool call, for the model's next turn.</summary>
    public static OpenAIChatMessage Tool(string toolCallId, string content) => new("tool", content) { ToolCallId = toolCallId };
}
