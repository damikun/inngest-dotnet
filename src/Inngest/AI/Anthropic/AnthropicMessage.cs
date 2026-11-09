// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using System.Text.Json.Serialization;

namespace Inngest;

/// <summary>A message of Anthropic's Messages API.</summary>
/// <param name="Role">user or assistant.</param>
/// <param name="Content">The blocks of the message.</param>
public sealed record AnthropicMessage(
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("content")] IReadOnlyList<AnthropicContentBlock> Content)
{
    /// <summary>A user message with one text block.</summary>
    public static AnthropicMessage User(string text) => new("user", [AnthropicContentBlock.FromText(text)]);

    /// <summary>An assistant message with one text block.</summary>
    public static AnthropicMessage Assistant(string text) => new("assistant", [AnthropicContentBlock.FromText(text)]);
}
