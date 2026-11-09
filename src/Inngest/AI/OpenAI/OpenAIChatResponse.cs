// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Inngest;

/// <summary>A response in the OpenAI chat completions format.</summary>
public sealed record OpenAIChatResponse
{
    [JsonPropertyName("id")]
    public string? Id { get; init; }

    [JsonPropertyName("object")]
    public string? Object { get; init; }

    /// <summary>When the response was made, in Unix seconds.</summary>
    [JsonPropertyName("created")]
    public long? Created { get; init; }

    [JsonPropertyName("model")]
    public string? Model { get; init; }

    [JsonPropertyName("choices")]
    public IReadOnlyList<OpenAIChatChoice> Choices { get; init; } = [];

    [JsonPropertyName("usage")]
    public OpenAIChatUsage? Usage { get; init; }

    [JsonPropertyName("system_fingerprint")]
    public string? SystemFingerprint { get; init; }

    [JsonPropertyName("service_tier")]
    public string? ServiceTier { get; init; }

    /// <summary>Set instead of the choices by providers that answer an error with a success status.</summary>
    [JsonPropertyName("error")]
    public OpenAIError? Error { get; init; }

    /// <summary>Properties this record does not have, sent and read as they are.</summary>
    [JsonExtensionData]
    public IDictionary<string, JsonElement>? AdditionalProperties { get; set; }

    /// <summary>The text of the first choice, or null when it has none.</summary>
    [JsonIgnore]
    public string? Text => Choices.Count > 0 ? Choices[0].Message?.Content : null;
}
