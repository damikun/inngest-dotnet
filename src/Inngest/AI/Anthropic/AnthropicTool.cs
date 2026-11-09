// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Inngest;

/// <summary>A tool the model may use, in Anthropic's Messages API.</summary>
/// <param name="Name">The name the model calls the tool by.</param>
/// <param name="InputSchema">The JSON schema of the tool's input.</param>
public sealed record AnthropicTool(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("input_schema")] JsonElement InputSchema)
{
    [JsonPropertyName("description")]
    public string? Description { get; init; }

    /// <summary>Properties this record does not have, sent and read as they are.</summary>
    [JsonExtensionData]
    public IDictionary<string, JsonElement>? AdditionalProperties { get; set; }
}
