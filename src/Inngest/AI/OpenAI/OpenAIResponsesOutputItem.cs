// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Inngest;

/// <summary>An item of the output of the OpenAI Responses API, such as a message, a function call or reasoning.</summary>
public sealed record OpenAIResponsesOutputItem
{
    /// <summary>message, function_call, reasoning and so on.</summary>
    [JsonPropertyName("type")]
    public string? Type { get; init; }

    [JsonPropertyName("id")]
    public string? Id { get; init; }

    [JsonPropertyName("status")]
    public string? Status { get; init; }

    [JsonPropertyName("role")]
    public string? Role { get; init; }

    /// <summary>The parts of a message.</summary>
    [JsonPropertyName("content")]
    public IReadOnlyList<OpenAIResponsesContent>? Content { get; init; }

    [JsonPropertyName("call_id")]
    public string? CallId { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    /// <summary>The arguments of a function call, as JSON text.</summary>
    [JsonPropertyName("arguments")]
    public string? Arguments { get; init; }

    /// <summary>Properties this record does not have, sent and read as they are.</summary>
    [JsonExtensionData]
    public IDictionary<string, JsonElement>? AdditionalProperties { get; set; }
}
