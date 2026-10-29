// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Inngest.Rest;

[JsonSerializable(typeof(RunReply))]
[JsonSerializable(typeof(RunListReply))]
internal sealed partial class RestJsonContext : JsonSerializerContext
{
}

// GET /v1/runs/{id} answers {"data":{...},"metadata":{"fetched_at":...,"cached_until":...}}; the metadata is not read.
internal sealed class RunReply
{
    [JsonPropertyName("data")]
    public RunData? Data { get; init; }
}

internal sealed class RunListReply
{
    [JsonPropertyName("data")]
    public RunData[]? Data { get; init; }
}

// The status stays a string so that a status Inngest adds later reads as Unknown instead of failing the call.
internal sealed class RunData
{
    [JsonPropertyName("run_id")]
    public string? RunId { get; init; }

    [JsonPropertyName("status")]
    public string? Status { get; init; }

    [JsonPropertyName("function_id")]
    public string? FunctionId { get; init; }

    [JsonPropertyName("function_version")]
    public int FunctionVersion { get; init; }

    [JsonPropertyName("run_started_at")]
    public DateTimeOffset StartedAt { get; init; }

    [JsonPropertyName("ended_at")]
    public DateTimeOffset? EndedAt { get; init; }

    [JsonPropertyName("event_id")]
    public string? EventId { get; init; }

    [JsonPropertyName("batch_id")]
    public string? BatchId { get; init; }

    [JsonPropertyName("original_run_id")]
    public string? OriginalRunId { get; init; }

    [JsonPropertyName("cron")]
    public string? Cron { get; init; }

    [JsonPropertyName("environment_id")]
    public string? EnvironmentId { get; init; }

    [JsonPropertyName("output")]
    public JsonElement Output { get; init; }
}
