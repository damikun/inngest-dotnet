// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Inngest;

/// <summary>A function call that Gemini asks for.</summary>
public sealed record GeminiFunctionCall(
    [property: JsonPropertyName("name")] string Name)
{
    [JsonPropertyName("id")]
    public string? Id { get; init; }

    [JsonPropertyName("args")]
    public JsonElement? Args { get; init; }

    /// <summary>Properties this record does not have, sent and read as they are.</summary>
    [JsonExtensionData]
    public IDictionary<string, JsonElement>? AdditionalProperties { get; set; }
}
