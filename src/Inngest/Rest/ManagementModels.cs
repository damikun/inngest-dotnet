// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using System.Text.Json;

namespace Inngest;

/// <summary>A function as the v2 REST API describes it.</summary>
public sealed record FunctionInfo
{
    /// <summary>The id the app registered the function with, without the app id in front.</summary>
    public required string Id { get; init; }

    public string Name { get; init; } = string.Empty;

    /// <summary>The id of the app the function belongs to.</summary>
    public string? AppId { get; init; }

    /// <summary>Whether the function is paused: its new runs wait (dev server) or are skipped (Inngest Cloud).</summary>
    public bool IsPaused { get; init; }

    public bool IsArchived { get; init; }

    public IReadOnlyList<FunctionTriggerInfo> Triggers { get; init; } = [];
}

/// <summary>A trigger of a function: an event name, or a cron schedule, with the expression that filters events.</summary>
/// <param name="Type"><c>EVENT</c> or <c>CRON</c>, as Inngest names it.</param>
/// <param name="Value">The event name or the cron schedule.</param>
/// <param name="If">The expression an event must match, if any.</param>
public sealed record FunctionTriggerInfo(string Type, string Value, string? If = null);

/// <summary>The outcome of pausing or resuming one of several functions: the function, or why it failed.</summary>
public sealed record FunctionPauseResult
{
    /// <summary>The function id as it was sent.</summary>
    public required string FunctionId { get; init; }

    /// <summary>The function after the change; null when <see cref="Error"/> says why it failed.</summary>
    public FunctionInfo? Function { get; init; }

    public InngestApiError? Error { get; init; }

    public bool Succeeded => Error is null;
}

/// <summary>An error of the v2 REST API: a code such as <c>not_found</c>, and a message.</summary>
public sealed record InngestApiError(string Code, string Message);

/// <summary>
/// Which runs of a function to cancel: those queued in a time range, and optionally only those whose triggering event
/// matches an expression.
/// </summary>
/// <param name="From">The earliest time a run was queued, inclusive.</param>
/// <param name="Until">The latest time a run was queued, inclusive; not in the future.</param>
public sealed record CancelRunsRequest(DateTimeOffset From, DateTimeOffset Until)
{
    /// <summary>An expression over <c>event</c>, the triggering event, such as <c>event.data.userId == "u-1"</c>.</summary>
    public string? If { get; init; }

    /// <summary>A name for the cancellation, up to 255 characters.</summary>
    public string? Name { get; init; }
}

/// <summary>A bulk cancellation that Inngest accepted. It runs in the background.</summary>
public sealed record RunCancellation
{
    public required string Id { get; init; }

    public string AppId { get; init; } = string.Empty;

    public string FunctionId { get; init; } = string.Empty;

    public string? Name { get; init; }

    public DateTimeOffset From { get; init; }

    public DateTimeOffset Until { get; init; }

    public string? If { get; init; }

    public DateTimeOffset CreatedAt { get; init; }
}

/// <summary>An event Inngest received, as the v2 REST API describes it.</summary>
public sealed record EventInfo
{
    /// <summary>Inngest's id of the event, the one sending it returned.</summary>
    public required string Id { get; init; }

    public string Name { get; init; } = string.Empty;

    public DateTimeOffset ReceivedAt { get; init; }

    /// <summary>The time the sender gave the event (<c>ts</c>), if any.</summary>
    public DateTimeOffset? OccurredAt { get; init; }

    /// <summary>The <c>id</c> the sender gave the event to deduplicate it, if any.</summary>
    public string? IdempotencyKey { get; init; }

    public string? Version { get; init; }

    /// <summary>The event as it was stored (<c>name</c>, <c>data</c>, <c>user</c>, <c>ts</c>, <c>id</c>, <c>v</c>),
    /// when it was asked for; otherwise its ValueKind is Undefined.</summary>
    public JsonElement Payload { get; init; }
}

/// <summary>Which events <see cref="IInngestManagementClient.ListEventsAsync"/> lists, newest first.</summary>
public sealed record EventQuery
{
    /// <summary>The earliest time an event was received; an hour before <see cref="Until"/> when null.</summary>
    public DateTimeOffset? From { get; init; }

    /// <summary>The latest time an event was received; now when null.</summary>
    public DateTimeOffset? Until { get; init; }

    /// <summary>Only events with these names, up to 20.</summary>
    public IReadOnlyList<string>? Names { get; init; }

    /// <summary>Fills in <see cref="EventInfo.Payload"/>. Pages are then at most 5 events long.</summary>
    public bool IncludePayload { get; init; }

    /// <summary>How many events each request asks for: 1 to 50, or 1 to 5 with the payload; Inngest's default when null.</summary>
    public int? PageSize { get; init; }
}

/// <summary>A sync of an app: when Inngest read the app's functions, and how that went.</summary>
public sealed record AppSync
{
    public required string Id { get; init; }

    public string AppId { get; init; } = string.Empty;

    /// <summary><c>pending</c>, <c>success</c>, <c>error</c> or <c>duplicate</c>, as Inngest names it.</summary>
    public string Status { get; init; } = string.Empty;

    public string? Url { get; init; }

    public string? SdkLanguage { get; init; }

    public string? SdkVersion { get; init; }

    public string? Framework { get; init; }

    public string? AppVersion { get; init; }

    public InngestApiError? Error { get; init; }

    public DateTimeOffset CreatedAt { get; init; }
}

/// <summary>A run as the run lists of the v2 REST API describe it.</summary>
public sealed record RunSummary
{
    public required string RunId { get; init; }

    public required FunctionRunStatus Status { get; init; }

    /// <summary>The id the app registered the function with, without the app id in front.</summary>
    public string FunctionId { get; init; } = string.Empty;

    public string? FunctionName { get; init; }

    public string? AppId { get; init; }

    public DateTimeOffset QueuedAt { get; init; }

    public DateTimeOffset? StartedAt { get; init; }

    public DateTimeOffset? EndedAt { get; init; }

    /// <summary>The ids of the events that started the run, more than one for a batch.</summary>
    public IReadOnlyList<string> EventIds { get; init; } = [];

    public string? EventName { get; init; }

    public string? BatchId { get; init; }

    /// <summary>The schedule that started the run.</summary>
    public string? Cron { get; init; }

    /// <summary>
    /// What the run returned, when <see cref="RunQuery.IncludeOutput"/> asked for it. Inngest wraps output that is not a
    /// JSON object as <c>{"data": ...}</c>. Its ValueKind is Undefined otherwise.
    /// </summary>
    public JsonElement Output { get; init; }
}

/// <summary>Which time of a run <see cref="RunQuery.From"/> and <see cref="RunQuery.Until"/> apply to.</summary>
public enum RunTimeField
{
    QueuedAt,
    StartedAt,
    EndedAt,
}

/// <summary>Which runs <see cref="IInngestManagementClient.ListRunsAsync"/> lists.</summary>
public sealed record RunQuery
{
    public DateTimeOffset? From { get; init; }

    public DateTimeOffset? Until { get; init; }

    /// <summary>The time of a run that <see cref="From"/> and <see cref="Until"/> apply to; when it was queued by default.</summary>
    public RunTimeField TimeField { get; init; } = RunTimeField.QueuedAt;

    /// <summary>Only runs with these statuses: Scheduled (queued), Running, Paused, Completed, Failed, Cancelled or Skipped.</summary>
    public IReadOnlyList<FunctionRunStatus>? Statuses { get; init; }

    /// <summary>Only runs of these apps, by app id.</summary>
    public IReadOnlyList<string>? AppIds { get; init; }

    /// <summary>Only runs of these functions, by the id the app registered them with. Needs <see cref="AppIds"/>.</summary>
    public IReadOnlyList<string>? FunctionIds { get; init; }

    /// <summary>An expression over the run's <c>event</c>, <c>output</c> and <c>error</c>, up to 2048 bytes.</summary>
    public string? Query { get; init; }

    /// <summary>Oldest first, instead of newest first.</summary>
    public bool Ascending { get; init; }

    /// <summary>Fills in <see cref="RunSummary.Output"/>.</summary>
    public bool IncludeOutput { get; init; }

    /// <summary>How many runs each request asks for, 1 to 100; Inngest's default when null.</summary>
    public int? PageSize { get; init; }
}
