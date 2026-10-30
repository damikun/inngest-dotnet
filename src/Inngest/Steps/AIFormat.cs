// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

namespace Inngest;

/// <summary>The provider APIs Inngest can call, as inngest-js names them.</summary>
public enum AIFormat
{
    OpenAIChat,
    OpenAIResponses,
    Anthropic,
    Gemini,
    Grok,
    AzureOpenAI,
}
