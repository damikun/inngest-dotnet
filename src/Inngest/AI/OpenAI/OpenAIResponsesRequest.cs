// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Inngest;

/// <summary>
/// The body of a request to the OpenAI Responses API. A model of AIModels fills in Model when it is null, and its
/// default parameters where the request has none.
/// </summary>
public sealed record OpenAIResponsesRequest
{
    [JsonPropertyName("model")]
    public string? Model { get; init; }

    /// <summary>The conversation so far: messages, and the function calls and their outputs.</summary>
    [JsonPropertyName("input")]
    public IReadOnlyList<OpenAIResponsesInputItem> Input { get; init; } = [];

    [JsonPropertyName("instructions")]
    public string? Instructions { get; init; }

    [JsonPropertyName("tools")]
    public IReadOnlyList<OpenAIResponsesTool>? Tools { get; init; }

    /// <summary>"auto", "none", "required", or an object that names a tool.</summary>
    [JsonPropertyName("tool_choice")]
    public JsonElement? ToolChoice { get; init; }

    [JsonPropertyName("parallel_tool_calls")]
    public bool? ParallelToolCalls { get; init; }

    [JsonPropertyName("temperature")]
    public double? Temperature { get; init; }

    [JsonPropertyName("top_p")]
    public double? TopP { get; init; }

    [JsonPropertyName("max_output_tokens")]
    public int? MaxOutputTokens { get; init; }

    [JsonPropertyName("reasoning")]
    public OpenAIResponsesReasoning? Reasoning { get; init; }

    /// <summary>The format of the text, such as {"format": {"type": "json_schema", ...}}, and its verbosity.</summary>
    [JsonPropertyName("text")]
    public JsonElement? Text { get; init; }

    [JsonPropertyName("previous_response_id")]
    public string? PreviousResponseId { get; init; }

    [JsonPropertyName("store")]
    public bool? Store { get; init; }

    [JsonPropertyName("metadata")]
    public IReadOnlyDictionary<string, string>? Metadata { get; init; }

    /// <summary>Properties this record does not have, sent and read as they are.</summary>
    [JsonExtensionData]
    public IDictionary<string, JsonElement>? AdditionalProperties { get; set; }
}
