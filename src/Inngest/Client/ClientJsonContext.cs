// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using System.Text.Json.Serialization;

namespace Inngest.Client;

[JsonSerializable(typeof(EventApiResponse))]
[JsonSerializable(typeof(ApiReply))]
internal sealed partial class ClientJsonContext : JsonSerializerContext
{
}

// The event API answers with the outcome in the body: {"ids":["01J..."],"status":200}. On partial failure the ids
// of the events that were not accepted are empty strings and error holds the first failure.
internal sealed class EventApiResponse
{
    [JsonPropertyName("ids")]
    public string[]? Ids { get; init; }

    [JsonPropertyName("status")]
    public int Status { get; init; }

    [JsonPropertyName("error")]
    public string? Error { get; init; }
}

// The parts of a REST API reply the SDK reads: the register reply says whether the sync changed anything, and
// error replies carry the reason in "error".
internal sealed class ApiReply
{
    [JsonPropertyName("modified")]
    public bool? Modified { get; init; }

    [JsonPropertyName("error")]
    public string? Error { get; init; }
}
