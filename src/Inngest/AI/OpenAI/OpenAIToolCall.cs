// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Inngest;

/// <summary>A function call that the model asks for.</summary>
public sealed record OpenAIToolCall(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("function")] OpenAIFunctionCall Function)
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = "function";

    /// <summary>Properties this record does not have, sent and read as they are.</summary>
    [JsonExtensionData]
    public IDictionary<string, JsonElement>? AdditionalProperties { get; set; }
}
