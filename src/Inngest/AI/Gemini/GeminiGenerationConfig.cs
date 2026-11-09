// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Inngest;

/// <summary>How Gemini generates its answer.</summary>
public sealed record GeminiGenerationConfig
{
    [JsonPropertyName("temperature")]
    public double? Temperature { get; init; }

    [JsonPropertyName("topP")]
    public double? TopP { get; init; }

    [JsonPropertyName("topK")]
    public int? TopK { get; init; }

    [JsonPropertyName("maxOutputTokens")]
    public int? MaxOutputTokens { get; init; }

    [JsonPropertyName("candidateCount")]
    public int? CandidateCount { get; init; }

    [JsonPropertyName("stopSequences")]
    public IReadOnlyList<string>? StopSequences { get; init; }

    [JsonPropertyName("presencePenalty")]
    public double? PresencePenalty { get; init; }

    [JsonPropertyName("frequencyPenalty")]
    public double? FrequencyPenalty { get; init; }

    [JsonPropertyName("seed")]
    public int? Seed { get; init; }

    /// <summary>Such as application/json for structured output.</summary>
    [JsonPropertyName("responseMimeType")]
    public string? ResponseMimeType { get; init; }

    [JsonPropertyName("responseSchema")]
    public JsonElement? ResponseSchema { get; init; }

    [JsonPropertyName("responseJsonSchema")]
    public JsonElement? ResponseJsonSchema { get; init; }

    [JsonPropertyName("thinkingConfig")]
    public GeminiThinkingConfig? ThinkingConfig { get; init; }

    /// <summary>Properties this record does not have, sent and read as they are.</summary>
    [JsonExtensionData]
    public IDictionary<string, JsonElement>? AdditionalProperties { get; set; }
}
