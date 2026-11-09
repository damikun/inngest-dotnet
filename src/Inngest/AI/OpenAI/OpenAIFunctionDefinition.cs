// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Inngest;

/// <summary>A function the model may call: its name, what it does and the JSON schema of its arguments.</summary>
public sealed record OpenAIFunctionDefinition(
    [property: JsonPropertyName("name")] string Name)
{
    [JsonPropertyName("description")]
    public string? Description { get; init; }

    /// <summary>The JSON schema of the arguments.</summary>
    [JsonPropertyName("parameters")]
    public JsonElement? Parameters { get; init; }

    [JsonPropertyName("strict")]
    public bool? Strict { get; init; }

    /// <summary>Properties this record does not have, sent and read as they are.</summary>
    [JsonExtensionData]
    public IDictionary<string, JsonElement>? AdditionalProperties { get; set; }
}
