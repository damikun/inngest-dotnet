// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using System.Text;

namespace Inngest;

/// <summary>A deployment of Azure OpenAI, which speaks the OpenAI chat completions format, for <see cref="AIModels.AzureOpenAI"/>.</summary>
/// <param name="Endpoint">The resource's endpoint, such as https://my-resource.openai.azure.com/.</param>
/// <param name="Deployment">The name of the deployment, which picks the model.</param>
/// <param name="ApiVersion">The api-version of the requests, such as 2024-10-21.</param>
public sealed record AzureOpenAIModelOptions(Uri Endpoint, string Deployment, string ApiVersion)
{
    /// <summary>The model a request gets when it names none. Azure goes by the deployment, so it may stay null.</summary>
    public string? Model { get; init; }

    /// <summary>The API key, sent in the api-key header. Null reads AZURE_OPENAI_API_KEY.</summary>
    public string? ApiKey { get; init; }

    /// <summary>What every request gets for the properties it leaves unset.</summary>
    public OpenAIChatRequest? DefaultParameters { get; init; }

    // Printing the options, for example into a log, must not reveal the key.
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append("Endpoint = ").Append(Endpoint).Append(", Deployment = ").Append(Deployment).Append(", ApiVersion = ").Append(ApiVersion);
        return true;
    }
}
