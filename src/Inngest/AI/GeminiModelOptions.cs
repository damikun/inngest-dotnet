// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using System.Text;

namespace Inngest;

/// <summary>A model of the Gemini API, for <see cref="AIModels.Gemini"/>.</summary>
/// <param name="Model">The model the requests go to, such as gemini-2.5-flash. It is part of the URL.</param>
public sealed record GeminiModelOptions(string Model)
{
    /// <summary>The API key. Null reads GEMINI_API_KEY.</summary>
    public string? ApiKey { get; init; }

    /// <summary>The base URL of the API. Null uses https://generativelanguage.googleapis.com/v1beta/.</summary>
    public Uri? BaseUrl { get; init; }

    /// <summary>
    /// What every request gets for the properties it leaves unset. The properties of GenerationConfig, and of its
    /// ThinkingConfig, are filled in one by one.
    /// </summary>
    public GeminiRequest? DefaultParameters { get; init; }

    // Printing the options, for example into a log, must not reveal the key.
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append("Model = ").Append(Model).Append(", BaseUrl = ").Append(BaseUrl);
        return true;
    }
}
