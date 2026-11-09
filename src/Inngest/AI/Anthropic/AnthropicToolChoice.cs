// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Inngest;

/// <summary>How the model uses the tools in Anthropic's Messages API.</summary>
/// <param name="Type">auto, any, tool or none.</param>
public sealed record AnthropicToolChoice(
    [property: JsonPropertyName("type")] string Type)
{
    /// <summary>The tool to use, with the type tool.</summary>
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("disable_parallel_tool_use")]
    public bool? DisableParallelToolUse { get; init; }

    /// <summary>Properties this record does not have, sent and read as they are.</summary>
    [JsonExtensionData]
    public IDictionary<string, JsonElement>? AdditionalProperties { get; set; }
}
