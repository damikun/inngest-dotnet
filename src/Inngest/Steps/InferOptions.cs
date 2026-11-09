// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using System.Text.Json.Serialization.Metadata;

namespace Inngest;

/// <summary>
/// A request that Inngest sends to an AI provider for the run. Body is the provider's request body, serialized with
/// InngestOptions.SerializerOptions. Inngest stores these options with the run, AuthKey included.
/// </summary>
public sealed record InferOptions(AIFormat Format, Uri Url, object Body)
{
    /// <summary>
    /// Inngest sends it as the format expects: "Authorization: Bearer" for the OpenAI formats, Grok and Azure OpenAI,
    /// "x-api-key" for Anthropic and the "key" query parameter for Gemini.
    /// </summary>
    public string? AuthKey { get; init; }

    /// <summary>Sent as they are, for example the "anthropic-version" header that Anthropic requires.</summary>
    public IReadOnlyDictionary<string, string>? Headers { get; init; }

    // How a model of AIModels reads the response, with contracts of the SDK's own rather than the app's options, which an
    // app under Native AOT has none for.
    internal JsonTypeInfo? ResponseType { get; init; }
}
