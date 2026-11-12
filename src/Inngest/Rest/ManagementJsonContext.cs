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
[JsonSerializable(typeof(V2Reply<V2FunctionPauseResult[]>))]
[JsonSerializable(typeof(V2Reply<V2Cancellation>))]
[JsonSerializable(typeof(V2Reply<V2Event>))]
[JsonSerializable(typeof(V2Reply<V2Event[]>))]
[JsonSerializable(typeof(V2Reply<V2Sync[]>))]
[JsonSerializable(typeof(V2Reply<V2Run[]>))]
[JsonSerializable(typeof(V2ErrorReply))]
[JsonSerializable(typeof(V2FunctionIds))]
[JsonSerializable(typeof(V2CancellationRequest))]
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

    public bool IsPaused { get; init; }

    public bool IsArchived { get; init; }

    public V2Ref? App { get; init; }

    public V2Trigger[]? Triggers { get; init; }
}

internal sealed class V2Ref
{
    public string? Id { get; init; }

    public string? Name { get; init; }
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
}

internal sealed class V2RunTrigger
{
    public string[]? EventIds { get; init; }

    public string? EventName { get; init; }

    public string? BatchId { get; init; }

    public string? CronSchedule { get; init; }
}
