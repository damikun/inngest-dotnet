// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Inngest;

/// <summary>
/// The body of a generateContent request to the Gemini API. The model is part of the URL, so the body has none. A model
/// of AIModels fills in its default parameters where the request has none.
/// </summary>
public sealed record GeminiRequest
{
    [JsonPropertyName("contents")]
    public IReadOnlyList<GeminiContent> Contents { get; init; } = [];

    [JsonPropertyName("systemInstruction")]
    public GeminiContent? SystemInstruction { get; init; }

    [JsonPropertyName("generationConfig")]
    public GeminiGenerationConfig? GenerationConfig { get; init; }

    [JsonPropertyName("tools")]
    public IReadOnlyList<GeminiTool>? Tools { get; init; }

    [JsonPropertyName("toolConfig")]
    public JsonElement? ToolConfig { get; init; }

    [JsonPropertyName("safetySettings")]
    public JsonElement? SafetySettings { get; init; }

    [JsonPropertyName("cachedContent")]
    public string? CachedContent { get; init; }

    /// <summary>Properties this record does not have, sent and read as they are.</summary>
    [JsonExtensionData]
    public IDictionary<string, JsonElement>? AdditionalProperties { get; set; }
}
