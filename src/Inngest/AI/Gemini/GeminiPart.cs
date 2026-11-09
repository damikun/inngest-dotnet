// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Inngest;

/// <summary>A part of a Gemini turn: text, inline data, a function call or a function response.</summary>
public sealed record GeminiPart
{
    [JsonPropertyName("text")]
    public string? Text { get; init; }

    /// <summary>Whether the text is the model's thinking.</summary>
    [JsonPropertyName("thought")]
    public bool? Thought { get; init; }

    [JsonPropertyName("inlineData")]
    public GeminiBlob? InlineData { get; init; }

    [JsonPropertyName("fileData")]
    public JsonElement? FileData { get; init; }

    [JsonPropertyName("functionCall")]
    public GeminiFunctionCall? FunctionCall { get; init; }

    [JsonPropertyName("functionResponse")]
    public GeminiFunctionResponse? FunctionResponse { get; init; }

    /// <summary>Properties this record does not have, sent and read as they are.</summary>
    [JsonExtensionData]
    public IDictionary<string, JsonElement>? AdditionalProperties { get; set; }

    /// <summary>A text part.</summary>
    public static GeminiPart FromText(string text) => new() { Text = text };
}
