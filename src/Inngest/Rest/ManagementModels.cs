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

    /// <summary>The slug Inngest derived from the function id. Empty when the reply left it out.</summary>
    public string? Slug { get; init; }

    /// <summary>
    /// The function's flow control as Inngest stored it (<c>retries</c>, <c>concurrency</c>, <c>throttle</c> and the
    /// rest). Its ValueKind is Undefined when the reply left it out.
    /// </summary>
    public JsonElement Configuration { get; init; }

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

/// <summary>The function a deferred run was handed to.</summary>
/// <param name="FunctionName">The function's name.</param>
/// <param name="FunctionSlug">The slug Inngest derived from the function id.</param>
public sealed record RunDeferredFrom(string? FunctionName, string? FunctionSlug);

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

    /// <summary>Whether the run was started by a batch of events.</summary>
    public bool IsBatch { get; init; }

    /// <summary>The schedule that started the run.</summary>
    public string? Cron { get; init; }

    /// <summary>How long the run took, in milliseconds, once it has ended.</summary>
    public long? DurationMs { get; init; }

    /// <summary>Whether the run was deferred to another function.</summary>
    public bool IsDeferred { get; init; }

    /// <summary>The function a deferred run was handed to, when <see cref="RunQuery.IncludeDeferredFrom"/> asked for it.</summary>
    public RunDeferredFrom? DeferredFrom { get; init; }

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

/// <summary>Which runs <see cref="IInngestManagementClient.ListRunsAsync"/> and <see cref="IInngestManagementClient.ListFunctionRunsAsync"/> list.</summary>
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

    /// <summary>Only deferred runs when true, and only runs that were not deferred when false. Null does not filter.</summary>
    public bool? Deferred { get; init; }

    /// <summary>An expression over the run's <c>event</c>, <c>output</c> and <c>error</c>, up to 2048 bytes.</summary>
    public string? Query { get; init; }

    /// <summary>Oldest first, instead of newest first.</summary>
    public bool Ascending { get; init; }

    /// <summary>Fills in <see cref="RunSummary.Output"/>.</summary>
    public bool IncludeOutput { get; init; }

    /// <summary>Fills in <see cref="RunSummary.DeferredFrom"/>, the function a deferred run was handed to.</summary>
    public bool IncludeDeferredFrom { get; init; }

    /// <summary>How many runs each request asks for, 1 to 100; Inngest's default when null.</summary>
    public int? PageSize { get; init; }
}

/// <summary>An app Inngest has synced.</summary>
public sealed record AppInfo
{
    /// <summary>The id the app registered with.</summary>
    public required string Id { get; init; }

    public string Name { get; init; } = string.Empty;

    public string? AppVersion { get; init; }

    public int FunctionCount { get; init; }

    public bool IsArchived { get; init; }

    public DateTimeOffset? CreatedAt { get; init; }

    public DateTimeOffset? ArchivedAt { get; init; }

    /// <summary>How the app connects: <c>SERVE</c>, <c>CONNECT</c>, <c>API</c> or <c>INNGEST_SERVE</c>.</summary>
    public string? Method { get; init; }

    public AppLatestSync? LatestSync { get; init; }
}

/// <summary>The latest sync of an app, as <see cref="AppInfo"/> reports it. The dev server has no sync history.</summary>
public sealed record AppLatestSync
{
    public string Status { get; init; } = string.Empty;

    public string? Url { get; init; }

    public string? SdkLanguage { get; init; }

    public string? SdkVersion { get; init; }

    public string? Framework { get; init; }

    public string? AppVersion { get; init; }

    public DateTimeOffset? SyncedAt { get; init; }

    /// <summary>The error text of a failed sync, when the reply included one.</summary>
    public string? Error { get; init; }
}

/// <summary>The account the signing key belongs to.</summary>
public sealed record AccountInfo
{
    public required string Id { get; init; }

    public string Name { get; init; } = string.Empty;

    public string Email { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset UpdatedAt { get; init; }
}

/// <summary>Data passed to <see cref="IInngestManagementClient.InvokeFunctionAsync"/>.</summary>
public sealed record InvokeRequest
{
    /// <summary>The event data the function receives. ValueKind Undefined sends none.</summary>
    public JsonElement Data { get; init; }

    /// <summary>A key Inngest uses so a repeat of this call does not start a second run.</summary>
    public string? IdempotencyKey { get; init; }
}

/// <summary>A function started through the REST API. <see cref="CompletedAt"/> is set when the run finished in the request.</summary>
public sealed record FunctionInvocation
{
    public required string RunId { get; init; }

    public DateTimeOffset? QueuedAt { get; init; }

    public DateTimeOffset? StartedAt { get; init; }

    public DateTimeOffset? CompletedAt { get; init; }

    /// <summary>The run's result, as the JSON text Inngest returned. Null when the run has not finished.</summary>
    public string? Result { get; init; }

    /// <summary>The error text, when the run failed before the request returned.</summary>
    public string? Error { get; init; }
}

/// <summary>A status <see cref="IInngestManagementClient.CreateReplayAsync"/> accepts. These are not the run list's statuses.</summary>
public enum ReplayStatus
{
    /// <summary>Every status a replay can select. It cannot be combined with another status.</summary>
    All,

    Completed,
    Failed,
    Cancelled,

    /// <summary>Runs that were skipped because the function was paused. Inngest names this <c>SKIPPED_PAUSED</c>.</summary>
    SkippedPaused,
}

/// <summary>Which runs of one function <see cref="IInngestManagementClient.CreateReplayAsync"/> replays.</summary>
/// <param name="Name">A name for the replay, between 3 and 64 characters.</param>
/// <param name="From">The earliest queued time, inclusive.</param>
/// <param name="Until">The latest queued time, inclusive. It may equal <paramref name="From"/>.</param>
/// <param name="Statuses">One to five statuses. <see cref="ReplayStatus.All"/> cannot be combined with another.</param>
public sealed record ReplayRequest(string Name, DateTimeOffset From, DateTimeOffset Until, IReadOnlyList<ReplayStatus> Statuses);

/// <summary>A replay Inngest accepted. It runs in the background.</summary>
public sealed record ReplayInfo
{
    public required string Id { get; init; }

    public string Name { get; init; } = string.Empty;

    public string FunctionId { get; init; } = string.Empty;

    public DateTimeOffset From { get; init; }

    public DateTimeOffset Until { get; init; }

    public IReadOnlyList<ReplayStatus> Statuses { get; init; } = [];

    public DateTimeOffset CreatedAt { get; init; }
}

/// <summary>Where <see cref="IInngestManagementClient.RerunAsync"/> starts the new run.</summary>
public sealed record RerunRequest
{
    /// <summary>The step id to rerun from. Null reruns the whole run.</summary>
    public string? StepId { get; init; }

    /// <summary>A JSON array that replaces that step's input. ValueKind Undefined sends none.</summary>
    public JsonElement Input { get; init; }
}

/// <summary>One score to record on a run, or on a step of it.</summary>
public sealed record RunScore
{
    /// <summary>The score name, at most 128 bytes.</summary>
    public required string Name { get; init; }

    /// <summary>A finite number or a boolean.</summary>
    public required JsonElement Value { get; init; }

    public string? StepId { get; init; }

    public string? ExperimentId { get; init; }

    public string? Variant { get; init; }
}

/// <summary>A score Inngest recorded.</summary>
public sealed record RunScoreResult
{
    public string RunId { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    public JsonElement Value { get; init; }

    public string? StepId { get; init; }

    public string? ExperimentId { get; init; }

    public string? Variant { get; init; }
}

/// <summary>The trace of one run: the function span and its steps.</summary>
public sealed record FunctionTrace
{
    public required string RunId { get; init; }

    public FunctionTraceSpan? Root { get; init; }
}

/// <summary>One span of a <see cref="FunctionTrace"/>.</summary>
public sealed record FunctionTraceSpan
{
    public string Id { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    /// <summary><c>RUNNING</c>, <c>COMPLETED</c>, <c>FAILED</c>, <c>WAITING</c>, <c>CANCELLED</c>, <c>SKIPPED</c>, or another value Inngest adds.</summary>
    public string Status { get; init; } = string.Empty;

    public string? StepId { get; init; }

    public string? StepOp { get; init; }

    public string? StepType { get; init; }

    public DateTimeOffset? QueuedAt { get; init; }

    public DateTimeOffset? StartedAt { get; init; }

    public DateTimeOffset? EndedAt { get; init; }

    public long? DurationMs { get; init; }

    /// <summary>The span's input, when it was asked for. ValueKind is Undefined otherwise.</summary>
    public JsonElement Input { get; init; }

    /// <summary>The span's output, when it was asked for. ValueKind is Undefined otherwise.</summary>
    public JsonElement Output { get; init; }

    public IReadOnlyList<FunctionTraceSpan> Children { get; init; } = [];
}

/// <summary>An event sent through the v2 REST API, for a test or a one-off send. High-volume sends use <see cref="IInngestClient"/>.</summary>
public sealed record EventDraft
{
    public required string Name { get; init; }

    public JsonElement Data { get; init; }

    /// <summary>An id Inngest uses to deduplicate the event.</summary>
    public string? Id { get; init; }

    /// <summary>When the event happened. Null sends no timestamp, and Inngest uses the time it received it.</summary>
    public DateTimeOffset? Timestamp { get; init; }

    public JsonElement User { get; init; }
}

/// <summary>Which experiments <see cref="IInngestManagementClient.ListExperimentsAsync"/> lists.</summary>
public sealed record ExperimentQuery
{
    public string? AppId { get; init; }

    public string? FunctionId { get; init; }

    public DateTimeOffset? From { get; init; }

    public DateTimeOffset? Until { get; init; }

    public int? PageSize { get; init; }
}

/// <summary>An experiment observed on a function.</summary>
public sealed record ExperimentInfo
{
    public required string Id { get; init; }

    public string? AppId { get; init; }

    public string? FunctionId { get; init; }

    public string? FunctionName { get; init; }

    public string? SelectionStrategy { get; init; }

    public int TotalRuns { get; init; }

    public int VariantCount { get; init; }

    public IReadOnlyList<string> Variants { get; init; } = [];

    public DateTimeOffset? FirstSeen { get; init; }

    public DateTimeOffset? LastSeen { get; init; }
}

/// <summary>One variant's share of an experiment.</summary>
/// <param name="Name">The variant name.</param>
/// <param name="Weight">Its weight.</param>
public sealed record ExperimentWeight(string Name, double Weight);

/// <summary>A score aggregate for one variant.</summary>
public sealed record ExperimentMetric
{
    public string Key { get; init; } = string.Empty;

    public double? Average { get; init; }

    public double? Min { get; init; }

    public double? Max { get; init; }
}

/// <summary>The runs and scores of one variant.</summary>
public sealed record ExperimentVariant
{
    public string Name { get; init; } = string.Empty;

    public int RunCount { get; init; }

    public IReadOnlyList<ExperimentMetric> Metrics { get; init; } = [];
}

/// <summary>An experiment with its variant weights and score aggregates.</summary>
public sealed record ExperimentDetail
{
    public required string Id { get; init; }

    public string? SelectionStrategy { get; init; }

    public DateTimeOffset? FirstSeen { get; init; }

    public DateTimeOffset? LastSeen { get; init; }

    public IReadOnlyList<ExperimentWeight> Weights { get; init; } = [];

    public IReadOnlyList<ExperimentVariant> Variants { get; init; } = [];
}

/// <summary>The shape Inngest has seen for one event name.</summary>
public sealed record InsightsEventSchema
{
    public required string Name { get; init; }

    public JsonElement Schema { get; init; }
}

/// <summary>A column of an Insights table.</summary>
public sealed record InsightsColumn
{
    public required string Name { get; init; }

    public string? Type { get; init; }

    public string? Description { get; init; }
}

/// <summary>A table <see cref="IInngestManagementClient.QueryInsightsAsync"/> can read.</summary>
public sealed record InsightsTable
{
    public required string Name { get; init; }

    public string? Description { get; init; }

    public IReadOnlyList<InsightsColumn> Columns { get; init; } = [];
}

/// <summary>A column of an Insights result.</summary>
public sealed record InsightsResultColumn
{
    public string Name { get; init; } = string.Empty;

    public string Type { get; init; } = string.Empty;
}

/// <summary>One diagnostic from an Insights query.</summary>
public sealed record InsightsDiagnostic
{
    public string? Code { get; init; }

    public string Message { get; init; } = string.Empty;

    public string? Severity { get; init; }
}

/// <summary>The rows of an Insights query.</summary>
public sealed record InsightsResult
{
    public IReadOnlyList<InsightsResultColumn> Columns { get; init; } = [];

    /// <summary>One array of values per row, in the order of <see cref="Columns"/>.</summary>
    public IReadOnlyList<IReadOnlyList<JsonElement>> Rows { get; init; } = [];

    public IReadOnlyList<InsightsDiagnostic> Diagnostics { get; init; } = [];

    public bool Limited { get; init; }

    public string? PrimaryTable { get; init; }

    public IReadOnlyList<string> Tables { get; init; } = [];
}

/// <summary>The SQL an Insights prompt produced.</summary>
public sealed record InsightsQuery
{
    public string Sql { get; init; } = string.Empty;

    public string? Summary { get; init; }
}

/// <summary>A custom environment.</summary>
public sealed record EnvironmentInfo
{
    public required string Id { get; init; }

    public string Name { get; init; } = string.Empty;

    /// <summary><c>PRODUCTION</c>, <c>TEST</c> or <c>BRANCH</c>, as Inngest names it.</summary>
    public string Type { get; init; } = string.Empty;

    public bool IsArchived { get; init; }

    public DateTimeOffset CreatedAt { get; init; }
}

/// <summary>Which events a webhook accepts.</summary>
public sealed record WebhookEventFilter
{
    public IReadOnlyList<string> Names { get; init; } = [];

    /// <summary><c>ALLOW</c> or <c>DENY</c>.</summary>
    public string? Filter { get; init; }
}

/// <summary>A webhook endpoint Inngest calls when events arrive.</summary>
public sealed record WebhookInfo
{
    public required string Id { get; init; }

    public string Name { get; init; } = string.Empty;

    public string? Url { get; init; }

    public string? Environment { get; init; }

    public string? Transform { get; init; }

    public string? Response { get; init; }

    public WebhookEventFilter? Events { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset UpdatedAt { get; init; }
}

/// <summary>A webhook to create.</summary>
public sealed record WebhookRequest
{
    public required string Name { get; init; }

    public string? Transform { get; init; }

    public string? Response { get; init; }

    public WebhookEventFilter? Events { get; init; }
}

/// <summary>A session key observed in the environment.</summary>
public sealed record SessionKeyInfo
{
    public required string Id { get; init; }

    public DateTimeOffset CreatedAt { get; init; }
}

/// <summary>Which sessions <see cref="IInngestManagementClient.ListSessionsAsync"/> lists.</summary>
public sealed record SessionQuery
{
    public string? Search { get; init; }

    public DateTimeOffset? From { get; init; }

    public DateTimeOffset? Until { get; init; }

    public int? PageSize { get; init; }
}

/// <summary>One session id under a session key.</summary>
public sealed record SessionInfo
{
    public required string Id { get; init; }

    public int RunCount { get; init; }

    public int FailedRunCount { get; init; }

    public double FailureRate { get; init; }

    public DateTimeOffset? LastActiveAt { get; init; }

    public IReadOnlyList<SessionFunction> Functions { get; init; } = [];
}

/// <summary>A function that ran inside a session.</summary>
/// <param name="Id">The function id the app registered, without the app id in front.</param>
/// <param name="Name">The function's name.</param>
public sealed record SessionFunction(string Id, string? Name);

/// <summary>A run that belongs to a session.</summary>
public sealed record SessionRun
{
    public required string Id { get; init; }

    public FunctionRunStatus Status { get; init; }

    public string? FunctionId { get; init; }

    public string? FunctionName { get; init; }

    public string? EventName { get; init; }

    public DateTimeOffset QueuedAt { get; init; }

    public DateTimeOffset? StartedAt { get; init; }

    public DateTimeOffset? EndedAt { get; init; }
}
