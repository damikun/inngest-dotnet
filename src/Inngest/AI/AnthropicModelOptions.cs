// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using System.Globalization;
using System.Text;

namespace Inngest;

/// <summary>A model of Anthropic's Messages API, for <see cref="AIModels.Anthropic"/>.</summary>
/// <param name="Model">The model a request gets when it names none, such as claude-sonnet-4-5.</param>
/// <param name="MaxTokens">
/// The max_tokens of a request that sets none, which Anthropic requires. One set in DefaultParameters wins over it.
/// </param>
public sealed record AnthropicModelOptions(string Model, int MaxTokens)
{
    /// <summary>The API key. Null reads ANTHROPIC_API_KEY.</summary>
    public string? ApiKey { get; init; }

    /// <summary>The base URL of the API. Null uses https://api.anthropic.com/v1/.</summary>
    public Uri? BaseUrl { get; init; }

    /// <summary>Beta features to turn on, sent joined in the anthropic-beta header.</summary>
    public IReadOnlyList<string>? BetaHeaders { get; init; }

    /// <summary>What every request gets for the properties it leaves unset.</summary>
    public AnthropicRequest? DefaultParameters { get; init; }

    // Printing the options, for example into a log, must not reveal the key.
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append(CultureInfo.InvariantCulture, $"Model = {Model}, MaxTokens = {MaxTokens}, BaseUrl = {BaseUrl}");
        return true;
    }
}
