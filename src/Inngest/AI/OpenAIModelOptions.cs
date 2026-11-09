// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using System.Text;

namespace Inngest;

/// <summary>
/// A model of OpenAI's chat completions API, or of a provider that speaks it: <see cref="AIModels.OpenAI"/>,
/// <see cref="AIModels.Grok"/> and <see cref="AIModels.DeepSeek"/>.
/// </summary>
/// <param name="Model">The model a request gets when it names none, such as gpt-4o-mini.</param>
public sealed record OpenAIModelOptions(string Model)
{
    /// <summary>The API key. Null reads the provider's variable: OPENAI_API_KEY, XAI_API_KEY or DEEPSEEK_API_KEY.</summary>
    public string? ApiKey { get; init; }

    /// <summary>The base URL of the API, which the path of the chat completions endpoint is added to. Null uses the provider's.</summary>
    public Uri? BaseUrl { get; init; }

    /// <summary>What every request gets for the properties it leaves unset.</summary>
    public OpenAIChatRequest? DefaultParameters { get; init; }

    // Printing the options, for example into a log, must not reveal the key.
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append("Model = ").Append(Model).Append(", BaseUrl = ").Append(BaseUrl);
        return true;
    }
}
