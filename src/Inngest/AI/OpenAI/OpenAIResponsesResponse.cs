// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Inngest;

/// <summary>A response of the OpenAI Responses API.</summary>
public sealed record OpenAIResponsesResponse
{
    [JsonPropertyName("id")]
    public string? Id { get; init; }

    [JsonPropertyName("object")]
    public string? Object { get; init; }

    /// <summary>When the response was made, in Unix seconds.</summary>
    [JsonPropertyName("created_at")]
    public long? CreatedAt { get; init; }

    [JsonPropertyName("status")]
    public string? Status { get; init; }

    [JsonPropertyName("model")]
    public string? Model { get; init; }

    [JsonPropertyName("output")]
    public IReadOnlyList<OpenAIResponsesOutputItem> Output { get; init; } = [];

    [JsonPropertyName("usage")]
    public OpenAIResponsesUsage? Usage { get; init; }

    [JsonPropertyName("error")]
    public OpenAIError? Error { get; init; }

    [JsonPropertyName("incomplete_details")]
    public JsonElement? IncompleteDetails { get; init; }

    /// <summary>Properties this record does not have, sent and read as they are.</summary>
    [JsonExtensionData]
    public IDictionary<string, JsonElement>? AdditionalProperties { get; set; }

    /// <summary>The text of the output_text parts of the output's messages, joined, or null when there are none.</summary>
    [JsonIgnore]
    public string? OutputText
    {
        get
        {
            var texts = Output
                .Where(static item => item.Type == "message" && item.Content is not null)
                .SelectMany(static item => item.Content!)
                .Where(static part => part.Type == "output_text" && part.Text is not null)
                .Select(static part => part.Text)
                .ToList();
            return texts.Count == 0 ? null : string.Concat(texts);
        }
    }
}
