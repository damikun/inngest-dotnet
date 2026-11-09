// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using System.Text;

namespace Inngest;

/// <summary>A model of the OpenAI Responses API, for <see cref="AIModels.OpenAIResponses"/>.</summary>
/// <param name="Model">The model a request gets when it names none, such as gpt-5-mini.</param>
public sealed record OpenAIResponsesModelOptions(string Model)
{
    /// <summary>The API key. Null reads OPENAI_API_KEY.</summary>
    public string? ApiKey { get; init; }

    /// <summary>The base URL of the API. Null uses https://api.openai.com/v1/.</summary>
    public Uri? BaseUrl { get; init; }

    /// <summary>What every request gets for the properties it leaves unset.</summary>
    public OpenAIResponsesRequest? DefaultParameters { get; init; }

    // Printing the options, for example into a log, must not reveal the key.
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append("Model = ").Append(Model).Append(", BaseUrl = ").Append(BaseUrl);
        return true;
    }
}
