// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Inngest;

/// <summary>How much a reasoning model of the OpenAI Responses API thinks, and whether it summarizes its reasoning.</summary>
public sealed record OpenAIResponsesReasoning
{
    /// <summary>Such as minimal, low, medium or high.</summary>
    [JsonPropertyName("effort")]
    public string? Effort { get; init; }

    /// <summary>Such as auto, concise or detailed.</summary>
    [JsonPropertyName("summary")]
    public string? Summary { get; init; }

    /// <summary>Properties this record does not have, sent and read as they are.</summary>
    [JsonExtensionData]
    public IDictionary<string, JsonElement>? AdditionalProperties { get; set; }
}
