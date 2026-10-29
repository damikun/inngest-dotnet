// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

namespace Inngest;

// A request that Inngest sends to an AI provider for the run. Body is the provider's request body, serialized with
// InngestOptions.SerializerOptions. Inngest stores these options with the run, AuthKey included.
public sealed record InferOptions(AIFormat Format, Uri Url, object Body)
{
    // Inngest sends it as the format expects: "Authorization: Bearer" for the OpenAI formats, Grok and Azure OpenAI,
    // "x-api-key" for Anthropic and the "key" query parameter for Gemini.
    public string? AuthKey { get; init; }

    // Sent as they are, for example the "anthropic-version" header that Anthropic requires.
    public IReadOnlyDictionary<string, string>? Headers { get; init; }
}

// The provider APIs Inngest can call, as inngest-js names them.
public enum AIFormat
{
    OpenAIChat,
    OpenAIResponses,
    Anthropic,
    Gemini,
    Grok,
    AzureOpenAI,
}
