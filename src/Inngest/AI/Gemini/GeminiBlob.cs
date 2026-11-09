// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using System.Text.Json.Serialization;

namespace Inngest;

/// <summary>Data sent inline in a Gemini part, such as an image.</summary>
/// <param name="MimeType">Such as image/png.</param>
/// <param name="Data">The data, base64 encoded.</param>
public sealed record GeminiBlob(
    [property: JsonPropertyName("mimeType")] string MimeType,
    [property: JsonPropertyName("data")] string Data)
{
}
