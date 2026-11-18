// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Inngest.Rest;

// The v2 REST API writes camelCase names, leaves out false, empty and zero values in most replies, writes 64-bit
// numbers as strings and enums as their short names (COMPLETED rather than FUNCTION_RUN_STATUS_COMPLETED). Enums stay
// strings here, so that a value Inngest adds later does not fail the call.
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, NumberHandling = JsonNumberHandling.AllowReadingFromString)]
[JsonSerializable(typeof(V2Reply<V2Function>))]
[JsonSerializable(typeof(V2Reply<V2Function[]>))]
[JsonSerializable(typeof(V2Reply<V2FunctionPauseResult[]>))]
[JsonSerializable(typeof(V2Reply<V2Cancellation>))]
[JsonSerializable(typeof(V2Reply<V2Event>))]
[JsonSerializable(typeof(V2Reply<V2Event[]>))]
[JsonSerializable(typeof(V2Reply<V2Sync>))]
[JsonSerializable(typeof(V2Reply<V2Sync[]>))]
[JsonSerializable(typeof(V2Reply<V2Run>))]
[JsonSerializable(typeof(V2Reply<V2Run[]>))]
[JsonSerializable(typeof(V2Reply<V2App>))]
[JsonSerializable(typeof(V2Reply<V2App[]>))]
[JsonSerializable(typeof(V2Reply<V2Account>))]
[JsonSerializable(typeof(V2Reply<V2RunId>))]
[JsonSerializable(typeof(V2Reply<V2Invoke>))]
[JsonSerializable(typeof(V2Reply<V2Replay>))]
[JsonSerializable(typeof(V2Reply<V2Trace>))]
[JsonSerializable(typeof(V2Reply<V2EventId>))]
[JsonSerializable(typeof(V2Reply<V2Score[]>))]
[JsonSerializable(typeof(V2Reply<V2Experiment[]>))]
[JsonSerializable(typeof(V2Reply<V2ExperimentDetail>))]
[JsonSerializable(typeof(V2Reply<V2InsightsSchema[]>))]
[JsonSerializable(typeof(V2Reply<V2InsightsQuery>))]
[JsonSerializable(typeof(V2Reply<V2InsightsPrompt>))]
[JsonSerializable(typeof(V2Reply<V2InsightsTable[]>))]
[JsonSerializable(typeof(V2Reply<V2Env>))]
[JsonSerializable(typeof(V2Reply<V2Env[]>))]
[JsonSerializable(typeof(V2Reply<V2Webhook>))]
[JsonSerializable(typeof(V2Reply<V2Webhook[]>))]
[JsonSerializable(typeof(V2Reply<V2SessionKey[]>))]
[JsonSerializable(typeof(V2Reply<V2Session[]>))]
[JsonSerializable(typeof(V2Reply<V2SessionRun[]>))]
[JsonSerializable(typeof(V2ErrorReply))]
[JsonSerializable(typeof(V2FunctionIds))]
[JsonSerializable(typeof(V2CancellationRequest))]
[JsonSerializable(typeof(V2SyncRequest))]
[JsonSerializable(typeof(V2InvokeRequest))]
[JsonSerializable(typeof(V2ReplayRequest))]
[JsonSerializable(typeof(V2RerunRequest))]
[JsonSerializable(typeof(V2ScoreInput[]))]
[JsonSerializable(typeof(V2EventWrite))]
[JsonSerializable(typeof(V2EnvCreate))]
[JsonSerializable(typeof(V2EnvPatch))]
[JsonSerializable(typeof(V2WebhookCreate))]
[JsonSerializable(typeof(V2QueryRequest))]
[JsonSerializable(typeof(V2PromptRequest))]
internal sealed partial class ManagementJsonContext : JsonSerializerContext
{
}

internal sealed class V2Reply<T>
{
    public T? Data { get; init; }

    public V2Metadata? Metadata { get; init; }

    public V2Page? Page { get; init; }
}

internal sealed class V2Metadata
{
    public DateTimeOffset? FetchedAt { get; init; }

    public V2TimeRange? TimeRange { get; init; }
}

internal sealed class V2TimeRange
{
    public DateTimeOffset? From { get; init; }

    public DateTimeOffset? Until { get; init; }
}

internal sealed class V2Page
{
    public string? Cursor { get; init; }

    public bool HasMore { get; init; }

    public int Limit { get; init; }
}

internal sealed class V2ErrorReply
{
    public V2Error[]? Errors { get; init; }
}

internal sealed class V2Error
{
    public string? Code { get; init; }

    public string? Message { get; init; }
}

internal sealed class V2Function
{
    public string? Id { get; init; }

    public string? Name { get; init; }

    public string? Slug { get; init; }

    public bool IsPaused { get; init; }

    public bool IsArchived { get; init; }

    public JsonElement Configuration { get; init; }

    public V2Ref? App { get; init; }

    public V2Trigger[]? Triggers { get; init; }
}

internal sealed class V2Ref
{
    public string? Id { get; init; }

    public string? Name { get; init; }

    public string? Slug { get; init; }

    public V2Ref? App { get; init; }
}

internal sealed class V2Trigger
{
    public string? Type { get; init; }

    public string? Value { get; init; }

    public string? If { get; init; }
}

internal sealed class V2FunctionPauseResult
{
    public string? FunctionId { get; init; }

    public V2Function? Function { get; init; }

    public V2Error? Error { get; init; }
}

internal sealed class V2FunctionIds
{
    public required string[] FunctionIds { get; init; }
}

// Times in UTC, which System.Text.Json writes with a Z, as RFC 3339 has it.
internal sealed class V2CancellationRequest
{
    public DateTime From { get; init; }

    public DateTime Until { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? If { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Name { get; init; }
}

internal sealed class V2Cancellation
{
    public string? Id { get; init; }

    public string? AppId { get; init; }

    public string? FunctionId { get; init; }

    public string? Name { get; init; }

    public DateTimeOffset From { get; init; }

    public DateTimeOffset Until { get; init; }

    public string? If { get; init; }

    public DateTimeOffset CreatedAt { get; init; }
}

internal sealed class V2Event
{
    public string? Id { get; init; }

    public string? Name { get; init; }

    public DateTimeOffset ReceivedAt { get; init; }

    public DateTimeOffset? OccurredAt { get; init; }

    public string? IdempotencyKey { get; init; }

    public string? Version { get; init; }

    public JsonElement Payload { get; init; }
}

internal sealed class V2Sync
{
    public string? Id { get; init; }

    public string? AppId { get; init; }

    public string? Status { get; init; }

    public string? Url { get; init; }

    public string? SdkLanguage { get; init; }

    public string? SdkVersion { get; init; }

    public string? Framework { get; init; }

    public string? AppVersion { get; init; }

    public V2Error? Error { get; init; }

    public DateTimeOffset CreatedAt { get; init; }
}

internal sealed class V2Run
{
    public string? Id { get; init; }

    public V2Ref? Function { get; init; }

    public V2Ref? App { get; init; }

    public string? Status { get; init; }

    public DateTimeOffset QueuedAt { get; init; }

    public DateTimeOffset? StartedAt { get; init; }

    public DateTimeOffset? EndedAt { get; init; }

    public V2RunTrigger? Trigger { get; init; }

    public JsonElement Output { get; init; }

    public string? DurationMs { get; init; }

    public bool IsDeferred { get; init; }

    public V2RunDeferredFrom? DeferredFrom { get; init; }
}

internal sealed class V2RunDeferredFrom
{
    public string? FunctionName { get; init; }

    public string? FunctionSlug { get; init; }
}

internal sealed class V2RunTrigger
{
    public string[]? EventIds { get; init; }

    public string? EventName { get; init; }

    public string? BatchId { get; init; }

    public string? CronSchedule { get; init; }

    public bool IsBatch { get; init; }
}

internal sealed class V2App
{
    public string? Id { get; init; }

    public string? Name { get; init; }

    public string? AppVersion { get; init; }

    public int FunctionCount { get; init; }

    public bool IsArchived { get; init; }

    public DateTimeOffset? CreatedAt { get; init; }

    public DateTimeOffset? ArchivedAt { get; init; }

    public string? Method { get; init; }

    public V2LatestSync? LatestSync { get; init; }
}

internal sealed class V2LatestSync
{
    public string? Status { get; init; }

    public string? Url { get; init; }

    public string? SdkLanguage { get; init; }

    public string? SdkVersion { get; init; }

    public string? Framework { get; init; }

    public string? AppVersion { get; init; }

    public DateTimeOffset? SyncedAt { get; init; }

    public string? Error { get; init; }
}

internal sealed class V2Account
{
    public string? Id { get; init; }

    public string? Name { get; init; }

    public string? Email { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset UpdatedAt { get; init; }
}

internal sealed class V2RunId
{
    public string? RunId { get; init; }
}

internal sealed class V2EventId
{
    public string? EventId { get; init; }
}

internal sealed class V2Invoke
{
    public string? RunId { get; init; }

    public DateTimeOffset? QueuedAt { get; init; }

    public DateTimeOffset? StartedAt { get; init; }

    public DateTimeOffset? CompletedAt { get; init; }

    public string? Result { get; init; }

    public string? Error { get; init; }
}

internal sealed class V2InvokeRequest
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public JsonElement Data { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? IdempotencyKey { get; init; }
}

internal sealed class V2SyncRequest
{
    public required string Url { get; init; }
}

internal sealed class V2Replay
{
    public string? Id { get; init; }

    public string? Name { get; init; }

    public string? FunctionId { get; init; }

    public DateTimeOffset From { get; init; }

    public DateTimeOffset Until { get; init; }

    public string[]? Statuses { get; init; }

    public DateTimeOffset CreatedAt { get; init; }
}

internal sealed class V2ReplayRequest
{
    public required string Name { get; init; }

    public DateTime From { get; init; }

    public DateTime Until { get; init; }

    public required string[] Statuses { get; init; }
}

internal sealed class V2RerunRequest
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public V2RerunFromStep? FromStep { get; init; }
}

internal sealed class V2RerunFromStep
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? StepId { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public JsonElement Input { get; init; }
}

internal sealed class V2Score
{
    public string? RunId { get; init; }

    public string? Name { get; init; }

    public JsonElement Value { get; init; }

    public string? StepId { get; init; }

    public V2ScoreExperiment? Experiment { get; init; }
}

internal sealed class V2ScoreInput
{
    public required string Name { get; init; }

    public JsonElement Value { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? StepId { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public V2ScoreExperiment? Experiment { get; init; }
}

internal sealed class V2ScoreExperiment
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Id { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Variant { get; init; }
}

internal sealed class V2Trace
{
    public string? RunId { get; init; }

    public V2TraceSpan? RootSpan { get; init; }
}

internal sealed class V2TraceSpan
{
    public string? Id { get; init; }

    public string? Name { get; init; }

    public string? Status { get; init; }

    public string? StepId { get; init; }

    public string? StepOp { get; init; }

    public string? StepType { get; init; }

    public DateTimeOffset? QueuedAt { get; init; }

    public DateTimeOffset? StartedAt { get; init; }

    public DateTimeOffset? EndedAt { get; init; }

    public string? DurationMs { get; init; }

    public JsonElement Input { get; init; }

    public JsonElement Output { get; init; }

    public V2TraceSpan[]? Children { get; init; }
}

internal sealed class V2EventWrite
{
    public required string Name { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public JsonElement Data { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Id { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? Ts { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public JsonElement User { get; init; }
}

internal sealed class V2Experiment
{
    public string? Id { get; init; }

    public V2Ref? Function { get; init; }

    public string? SelectionStrategy { get; init; }

    public int TotalRuns { get; init; }

    public int VariantCount { get; init; }

    public string[]? Variants { get; init; }

    public DateTimeOffset? FirstSeen { get; init; }

    public DateTimeOffset? LastSeen { get; init; }
}

internal sealed class V2ExperimentDetail
{
    public string? Id { get; init; }

    public string? SelectionStrategy { get; init; }

    public DateTimeOffset? FirstSeen { get; init; }

    public DateTimeOffset? LastSeen { get; init; }

    public V2ExperimentWeight[]? VariantWeights { get; init; }

    public V2ExperimentVariant[]? Variants { get; init; }
}

internal sealed class V2ExperimentWeight
{
    public string? VariantName { get; init; }

    public double Weight { get; init; }
}

internal sealed class V2ExperimentVariant
{
    public string? VariantName { get; init; }

    public int RunCount { get; init; }

    public V2ExperimentMetric[]? Metrics { get; init; }
}

internal sealed class V2ExperimentMetric
{
    public string? Key { get; init; }

    public double? Avg { get; init; }

    public double? Min { get; init; }

    public double? Max { get; init; }
}

internal sealed class V2InsightsSchema
{
    public string? Name { get; init; }

    public JsonElement Schema { get; init; }
}

internal sealed class V2InsightsTable
{
    public string? Name { get; init; }

    public string? Description { get; init; }

    public V2InsightsColumn[]? Columns { get; init; }
}

internal sealed class V2InsightsColumn
{
    public string? Name { get; init; }

    public string? Type { get; init; }

    public string? Description { get; init; }
}

internal sealed class V2InsightsQuery
{
    public V2InsightsResultColumn[]? Columns { get; init; }

    public V2InsightsRow[]? Rows { get; init; }

    public V2InsightsDiagnostic[]? Diagnostics { get; init; }

    public V2InsightsInfo? Info { get; init; }
}

internal sealed class V2InsightsResultColumn
{
    public string? Name { get; init; }

    public string? Type { get; init; }
}

internal sealed class V2InsightsRow
{
    public JsonElement[]? Values { get; init; }
}

internal sealed class V2InsightsDiagnostic
{
    public string? Code { get; init; }

    public string? Message { get; init; }

    public string? Severity { get; init; }
}

internal sealed class V2InsightsInfo
{
    public bool Limited { get; init; }

    public string? PrimaryTable { get; init; }

    public string[]? Tables { get; init; }
}

internal sealed class V2QueryRequest
{
    public required string Query { get; init; }
}

internal sealed class V2PromptRequest
{
    public required string Prompt { get; init; }
}

internal sealed class V2InsightsPrompt
{
    public string? Sql { get; init; }

    public string? Summary { get; init; }
}

internal sealed class V2Env
{
    public string? Id { get; init; }

    public string? Name { get; init; }

    public string? Type { get; init; }

    public bool IsArchived { get; init; }

    public DateTimeOffset CreatedAt { get; init; }
}

internal sealed class V2EnvCreate
{
    public required string Name { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Id { get; init; }
}

internal sealed class V2EnvPatch
{
    public bool IsArchived { get; init; }
}

internal sealed class V2Webhook
{
    public string? Id { get; init; }

    public string? Name { get; init; }

    public string? Url { get; init; }

    public string? Environment { get; init; }

    public string? Transform { get; init; }

    public string? Response { get; init; }

    public V2EventFilter? EventFilter { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset UpdatedAt { get; init; }
}

internal sealed class V2EventFilter
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string[]? Events { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Filter { get; init; }
}

internal sealed class V2WebhookCreate
{
    public required string Name { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Transform { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Response { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public V2EventFilter? EventFilter { get; init; }
}

internal sealed class V2SessionKey
{
    public string? Id { get; init; }

    public DateTimeOffset CreatedAt { get; init; }
}

internal sealed class V2Session
{
    public string? Id { get; init; }

    public int RunCount { get; init; }

    public int FailedRunCount { get; init; }

    public double FailureRate { get; init; }

    public DateTimeOffset? LastActiveAt { get; init; }

    public V2Ref[]? Functions { get; init; }
}

internal sealed class V2SessionRun
{
    public string? Id { get; init; }

    public string? Status { get; init; }

    public V2Ref? Function { get; init; }

    public string? EventName { get; init; }

    public DateTimeOffset QueuedAt { get; init; }

    public DateTimeOffset? StartedAt { get; init; }

    public DateTimeOffset? EndedAt { get; init; }
}
