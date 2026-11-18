// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

namespace Inngest;

/// <summary>
/// Manages apps, functions, runs, events, syncs, experiments, Insights, environments, webhooks and sessions through the
/// Inngest v2 REST API, authenticated with the app's signing
/// key and sent to the app's environment. Inngest marks these endpoints Beta. A dev server or a self-hosted server that
/// does not have an endpoint answers 501 or 404. Failures throw InngestException with Inngest's message and code, such
/// as <c>not_found</c>. Apps and functions are named by the ids the app registered them with, the function's without
/// the app id in front.
/// </summary>
public interface IInngestManagementClient
{
    /// <summary>
    /// Pauses a function: its new runs wait until it is resumed (dev server) or are skipped (Inngest Cloud, where the
    /// change takes up to a minute). Pausing a paused function changes nothing.
    /// </summary>
    Task<FunctionInfo> PauseFunctionAsync(string appId, string functionId, CancellationToken cancellationToken = default);

    /// <summary>Resumes a paused function. Resuming a function that is not paused changes nothing.</summary>
    Task<FunctionInfo> ResumeFunctionAsync(string appId, string functionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Pauses up to 500 functions of an app in one request, and returns an outcome for each, in the order given: a
    /// function that is not there fails on its own without failing the others.
    /// </summary>
    Task<IReadOnlyList<FunctionPauseResult>> PauseFunctionsAsync(string appId, IReadOnlyList<string> functionIds, CancellationToken cancellationToken = default);

    /// <summary>Resumes up to 500 functions of an app in one request, as <see cref="PauseFunctionsAsync"/> pauses them.</summary>
    Task<IReadOnlyList<FunctionPauseResult>> ResumeFunctionsAsync(string appId, IReadOnlyList<string> functionIds, CancellationToken cancellationToken = default);

    /// <summary>
    /// Cancels the runs of a function that were queued in a time range, and that match the expression if one is given.
    /// Inngest cancels them in the background: runs that have not started never start, running ones stop before their
    /// next step, and sleeping or waiting ones when they would resume. Each call creates another cancellation.
    /// </summary>
    Task<RunCancellation> CancelRunsAsync(string appId, string functionId, CancelRunsRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists the events Inngest received, newest first, a page at a time as the enumeration goes on. Without a time
    /// range, those of the last hour; every page uses the range of the first, so events that arrive meanwhile do not
    /// shift the pages.
    /// </summary>
    IAsyncEnumerable<EventInfo> ListEventsAsync(EventQuery? query = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets an event by the id that sending it returned, with its payload when asked for. Inngest stores an event a
    /// moment after it accepts it, and answers 404 (<c>not_found</c>) until then.
    /// </summary>
    Task<EventInfo> GetEventAsync(string eventId, bool includePayload = false, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists the syncs of an app, newest first, a page at a time as the enumeration goes on. The dev server and the
    /// self-hosted server answer 501 (not implemented) to this.
    /// </summary>
    IAsyncEnumerable<AppSync> ListAppSyncsAsync(string appId, int? pageSize = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists runs, newest first unless the query says otherwise, a page at a time as the enumeration goes on. The dev
    /// server and the self-hosted server answer 501 (not implemented) to a filter on the Paused status.
    /// </summary>
    IAsyncEnumerable<RunSummary> ListRunsAsync(RunQuery? query = null, CancellationToken cancellationToken = default);

    /// <summary>Lists apps in the environment. <paramref name="archived"/> lists archived apps instead of active ones.</summary>
    IAsyncEnumerable<AppInfo> ListAppsAsync(bool archived = false, int? pageSize = null, CancellationToken cancellationToken = default) =>
        throw Unsupported(nameof(ListAppsAsync));

    /// <summary>Gets one app, including its latest sync.</summary>
    Task<AppInfo> GetAppAsync(string appId, CancellationToken cancellationToken = default) =>
        throw Unsupported(nameof(GetAppAsync));

    /// <summary>Lists the functions of an app.</summary>
    IAsyncEnumerable<FunctionInfo> ListFunctionsAsync(string appId, int? pageSize = null, CancellationToken cancellationToken = default) =>
        throw Unsupported(nameof(ListFunctionsAsync));

    /// <summary>Gets one function, including its triggers and configuration.</summary>
    Task<FunctionInfo> GetFunctionAsync(string appId, string functionId, CancellationToken cancellationToken = default) =>
        throw Unsupported(nameof(GetFunctionAsync));

    /// <summary>Gets one sync of an app. The dev server answers 501.</summary>
    Task<AppSync> GetAppSyncAsync(string appId, string syncId, CancellationToken cancellationToken = default) =>
        throw Unsupported(nameof(GetAppSyncAsync));

    /// <summary>Syncs an app at an absolute http or https URL.</summary>
    Task<AppSync> SyncAppAsync(string appId, string url, CancellationToken cancellationToken = default) =>
        throw Unsupported(nameof(SyncAppAsync));

    /// <summary>Gets one run. <paramref name="includeOutput"/> fills in <see cref="RunSummary.Output"/>.</summary>
    Task<RunSummary> GetRunAsync(string runId, bool includeOutput = false, CancellationToken cancellationToken = default) =>
        throw Unsupported(nameof(GetRunAsync));

    /// <summary>Lists the runs an event started. <paramref name="includeOutput"/> fills in each run's output.</summary>
    IAsyncEnumerable<RunSummary> ListEventRunsAsync(string eventId, bool includeOutput = false, int? pageSize = null, CancellationToken cancellationToken = default) =>
        throw Unsupported(nameof(ListEventRunsAsync));

    /// <summary>
    /// Lists the runs of one function. <paramref name="query"/> filters them as <see cref="ListRunsAsync"/> does;
    /// its app and function ids are not sent, because the path already names the function.
    /// </summary>
    IAsyncEnumerable<RunSummary> ListFunctionRunsAsync(string appId, string functionId, RunQuery? query = null, CancellationToken cancellationToken = default) =>
        throw Unsupported(nameof(ListFunctionRunsAsync));

    /// <summary>Cancels one run. A step that is running finishes, and no further steps run.</summary>
    Task CancelRunAsync(string runId, CancellationToken cancellationToken = default) =>
        throw Unsupported(nameof(CancelRunAsync));

    /// <summary>Starts a new run from the original event. <paramref name="request"/> reruns from one step.</summary>
    Task<string> RerunAsync(string runId, RerunRequest? request = null, CancellationToken cancellationToken = default) =>
        throw Unsupported(nameof(RerunAsync));

    /// <summary>
    /// Replays matching runs of one function, in the background. Statuses are <c>ALL</c>, <c>COMPLETED</c>,
    /// <c>FAILED</c>, <c>CANCELLED</c> and <c>SKIPPED_PAUSED</c>. Each call creates another replay.
    /// </summary>
    Task<ReplayInfo> CreateReplayAsync(string appId, string functionId, ReplayRequest request, CancellationToken cancellationToken = default) =>
        throw Unsupported(nameof(CreateReplayAsync));

    /// <summary>Starts a function with the given data. The run may still be going when this returns.</summary>
    Task<FunctionInvocation> InvokeFunctionAsync(string appId, string functionId, InvokeRequest? request = null, CancellationToken cancellationToken = default) =>
        throw Unsupported(nameof(InvokeFunctionAsync));

    /// <summary>Records scores on a run, or on steps of it when a score names a step.</summary>
    Task<IReadOnlyList<RunScoreResult>> CreateScoresAsync(string runId, IReadOnlyList<RunScore> scores, CancellationToken cancellationToken = default) =>
        throw Unsupported(nameof(CreateScoresAsync));

    /// <summary>Gets the trace of a run. <paramref name="includeIO"/> includes each span's input and output.</summary>
    Task<FunctionTrace> GetRunTraceAsync(string runId, bool includeIO = false, CancellationToken cancellationToken = default) =>
        throw Unsupported(nameof(GetRunTraceAsync));

    /// <summary>Gets the account the signing key belongs to.</summary>
    Task<AccountInfo> GetAccountAsync(CancellationToken cancellationToken = default) =>
        throw Unsupported(nameof(GetAccountAsync));

    /// <summary>
    /// Sends one event through the REST API and returns its id. This is rate limited; send volume through
    /// <see cref="IInngestClient"/>.
    /// </summary>
    Task<string> SendEventAsync(EventDraft draft, CancellationToken cancellationToken = default) =>
        throw Unsupported(nameof(SendEventAsync));

    /// <summary>Lists experiments in the environment, optionally for one app or function.</summary>
    IAsyncEnumerable<ExperimentInfo> ListExperimentsAsync(ExperimentQuery? query = null, CancellationToken cancellationToken = default) =>
        throw Unsupported(nameof(ListExperimentsAsync));

    /// <summary>
    /// Lists experiments observed on one function. <paramref name="query"/> supplies the time range and page size.
    /// Its app and function ids are not sent, because the path already names the function.
    /// </summary>
    IAsyncEnumerable<ExperimentInfo> ListFunctionExperimentsAsync(string appId, string functionId, ExperimentQuery? query = null, CancellationToken cancellationToken = default) =>
        throw Unsupported(nameof(ListFunctionExperimentsAsync));

    /// <summary>Gets one experiment's variant weights and score aggregates.</summary>
    Task<ExperimentDetail> GetExperimentAsync(string appId, string functionId, string experimentId, string? variant = null, DateTimeOffset? from = null, DateTimeOffset? until = null, CancellationToken cancellationToken = default) =>
        throw Unsupported(nameof(GetExperimentAsync));

    /// <summary>Lists the event schemas Insights has seen.</summary>
    IAsyncEnumerable<InsightsEventSchema> ListInsightsSchemasAsync(int? pageSize = null, CancellationToken cancellationToken = default) =>
        throw Unsupported(nameof(ListInsightsSchemasAsync));

    /// <summary>Runs an Insights SQL query.</summary>
    Task<InsightsResult> QueryInsightsAsync(string query, CancellationToken cancellationToken = default) =>
        throw Unsupported(nameof(QueryInsightsAsync));

    /// <summary>Turns a prompt into an Insights SQL query. It does not run the query.</summary>
    Task<InsightsQuery> CreateInsightsQueryAsync(string prompt, CancellationToken cancellationToken = default) =>
        throw Unsupported(nameof(CreateInsightsQueryAsync));

    /// <summary>Lists the tables an Insights query can read.</summary>
    Task<IReadOnlyList<InsightsTable>> ListInsightsTablesAsync(CancellationToken cancellationToken = default) =>
        throw Unsupported(nameof(ListInsightsTablesAsync));

    /// <summary>Lists custom environments.</summary>
    IAsyncEnumerable<EnvironmentInfo> ListEnvironmentsAsync(int? pageSize = null, CancellationToken cancellationToken = default) =>
        throw Unsupported(nameof(ListEnvironmentsAsync));

    /// <summary>Creates a custom environment. <paramref name="id"/> is optional; Inngest assigns one when it is null.</summary>
    Task<EnvironmentInfo> CreateEnvironmentAsync(string name, string? id = null, CancellationToken cancellationToken = default) =>
        throw Unsupported(nameof(CreateEnvironmentAsync));

    /// <summary>Archives or restores an environment.</summary>
    Task<EnvironmentInfo> UpdateEnvironmentAsync(string id, bool archived, CancellationToken cancellationToken = default) =>
        throw Unsupported(nameof(UpdateEnvironmentAsync));

    /// <summary>
    /// Lists webhooks. Outside dev mode the environment must be set on the app: these endpoints require
    /// <c>X-Inngest-Env</c>.
    /// </summary>
    IAsyncEnumerable<WebhookInfo> ListWebhooksAsync(int? pageSize = null, CancellationToken cancellationToken = default) =>
        throw Unsupported(nameof(ListWebhooksAsync));

    /// <summary>Creates a webhook. Outside dev mode the environment must be set, as for <see cref="ListWebhooksAsync"/>.</summary>
    Task<WebhookInfo> CreateWebhookAsync(WebhookRequest request, CancellationToken cancellationToken = default) =>
        throw Unsupported(nameof(CreateWebhookAsync));

    /// <summary>Lists session keys. <paramref name="search"/> matches part of a key.</summary>
    IAsyncEnumerable<SessionKeyInfo> ListSessionKeysAsync(string? search = null, int? pageSize = null, CancellationToken cancellationToken = default) =>
        throw Unsupported(nameof(ListSessionKeysAsync));

    /// <summary>Lists the session ids of one session key.</summary>
    IAsyncEnumerable<SessionInfo> ListSessionsAsync(string sessionKey, SessionQuery? query = null, CancellationToken cancellationToken = default) =>
        throw Unsupported(nameof(ListSessionsAsync));

    /// <summary>Lists the runs of one session.</summary>
    IAsyncEnumerable<SessionRun> ListSessionRunsAsync(string sessionKey, string sessionId, int? pageSize = null, DateTimeOffset? from = null, DateTimeOffset? until = null, CancellationToken cancellationToken = default) =>
        throw Unsupported(nameof(ListSessionRunsAsync));

    private NotSupportedException Unsupported(string method) =>
        new($"{GetType().Name} does not implement {method}.");
}
