// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Inngest;

/// <summary>A turn of a conversation with Gemini.</summary>
public sealed record GeminiContent(
    [property: JsonPropertyName("parts")] IReadOnlyList<GeminiPart> Parts)
{
    /// <summary>user or model.</summary>
    [JsonPropertyName("role")]
    public string? Role { get; init; }

    /// <summary>Properties this record does not have, sent and read as they are.</summary>
    [JsonExtensionData]
    public IDictionary<string, JsonElement>? AdditionalProperties { get; set; }

    /// <summary>A user turn with one text part.</summary>
    public static GeminiContent User(string text) => new([GeminiPart.FromText(text)]) { Role = "user" };

    /// <summary>A model turn with one text part.</summary>
    public static GeminiContent Model(string text) => new([GeminiPart.FromText(text)]) { Role = "model" };
}
