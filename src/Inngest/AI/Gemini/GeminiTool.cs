// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Inngest;

/// <summary>Tools Gemini may use: functions, or a built-in tool such as googleSearch in AdditionalProperties.</summary>
public sealed record GeminiTool
{
    [JsonPropertyName("functionDeclarations")]
    public IReadOnlyList<GeminiFunctionDeclaration>? FunctionDeclarations { get; init; }

    /// <summary>Properties this record does not have, sent and read as they are.</summary>
    [JsonExtensionData]
    public IDictionary<string, JsonElement>? AdditionalProperties { get; set; }
}
