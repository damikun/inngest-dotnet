// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Inngest;

/// <summary>
/// A tool of the OpenAI Responses API: a function, or a built-in tool such as web_search, whose other settings go in
/// AdditionalProperties.
/// </summary>
/// <param name="Type">function, or the type of a built-in tool.</param>
public sealed record OpenAIResponsesTool(
    [property: JsonPropertyName("type")] string Type)
{
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    /// <summary>The JSON schema of a function's arguments.</summary>
    [JsonPropertyName("parameters")]
    public JsonElement? Parameters { get; init; }

    [JsonPropertyName("strict")]
    public bool? Strict { get; init; }

    /// <summary>Properties this record does not have, sent and read as they are.</summary>
    [JsonExtensionData]
    public IDictionary<string, JsonElement>? AdditionalProperties { get; set; }

    /// <summary>A function the model may call.</summary>
    public static OpenAIResponsesTool Function(string name, string? description = null, JsonElement? parameters = null) =>
        new("function") { Name = name, Description = description, Parameters = parameters };
}
