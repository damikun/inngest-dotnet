// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Inngest;

/// <summary>A breakdown of the input tokens of an OpenAI response.</summary>
public sealed record OpenAIInputTokensDetails
{
    /// <summary>Input tokens read from the prompt cache, which are part of the input tokens.</summary>
    [JsonPropertyName("cached_tokens")]
    public int? CachedTokens { get; init; }

    [JsonPropertyName("audio_tokens")]
    public int? AudioTokens { get; init; }

    /// <summary>Properties this record does not have, sent and read as they are.</summary>
    [JsonExtensionData]
    public IDictionary<string, JsonElement>? AdditionalProperties { get; set; }
}
