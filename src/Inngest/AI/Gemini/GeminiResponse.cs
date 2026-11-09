// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Inngest;

/// <summary>A response of the Gemini API's generateContent.</summary>
public sealed record GeminiResponse
{
    [JsonPropertyName("candidates")]
    public IReadOnlyList<GeminiCandidate> Candidates { get; init; } = [];

    [JsonPropertyName("usageMetadata")]
    public GeminiUsageMetadata? UsageMetadata { get; init; }

    [JsonPropertyName("modelVersion")]
    public string? ModelVersion { get; init; }

    [JsonPropertyName("responseId")]
    public string? ResponseId { get; init; }

    /// <summary>Why the prompt was blocked, when it was.</summary>
    [JsonPropertyName("promptFeedback")]
    public JsonElement? PromptFeedback { get; init; }

    [JsonPropertyName("error")]
    public GeminiError? Error { get; init; }

    /// <summary>Properties this record does not have, sent and read as they are.</summary>
    [JsonExtensionData]
    public IDictionary<string, JsonElement>? AdditionalProperties { get; set; }

    /// <summary>The text of the first candidate's parts, thoughts left out, or null when there is none.</summary>
    [JsonIgnore]
    public string? Text
    {
        get
        {
            var parts = Candidates.Count > 0 ? Candidates[0].Content?.Parts : null;
            var texts = parts?.Where(static part => part.Text is not null && part.Thought != true).Select(static part => part.Text).ToList();
            return texts is null || texts.Count == 0 ? null : string.Concat(texts);
        }
    }
}
