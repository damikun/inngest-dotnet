// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Inngest;

/// <summary>How much a Gemini thinking model thinks, and whether it returns its thoughts.</summary>
public sealed record GeminiThinkingConfig
{
    [JsonPropertyName("thinkingBudget")]
    public int? ThinkingBudget { get; init; }

    [JsonPropertyName("includeThoughts")]
    public bool? IncludeThoughts { get; init; }

    /// <summary>Properties this record does not have, sent and read as they are.</summary>
    [JsonExtensionData]
    public IDictionary<string, JsonElement>? AdditionalProperties { get; set; }
}
