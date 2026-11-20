// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Inngest.Rest;

internal sealed partial class InngestManagementClient
{
    private const int MaxScoreNameBytes = 128;

    private const int MaxSpanDepth = 64;

    private static readonly byte[] EmptyObject = "{}"u8.ToArray();

    public IAsyncEnumerable<AppInfo> ListAppsAsync(bool archived = false, int? pageSize = null, CancellationToken cancellationToken = default) =>
        EachPageAsync(
            "list apps",
            _json.V2ReplyV2AppArray,
            cursor => "/v2/apps" + Page(cursor, pageSize, ("archived", archived ? "true" : null)),
            ToApp,
            cancellationToken);

    public async Task<AppInfo> GetAppAsync(string appId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(appId);
        var reply = await SendAsync(HttpMethod.Get, $"/v2/apps/{Escape(appId)}", null, _json.V2ReplyV2App, $"get app {appId}", cancellationToken).ConfigureAwait(false);
        return reply.Data is { Id: { Length: > 0 } } app ? ToApp(app) : throw NoData($"get app {appId}");
    }

    public IAsyncEnumerable<FunctionInfo> ListFunctionsAsync(string appId, int? pageSize = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(appId);
        return EachPageAsync(
            $"list the functions of app {appId}",
            _json.V2ReplyV2FunctionArray,
            cursor => $"/v2/apps/{Escape(appId)}/functions" + Page(cursor, pageSize),
            function => ToFunction(function, appId),
            cancellationToken);
    }

    public async Task<FunctionInfo> GetFunctionAsync(string appId, string functionId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(appId);
        ArgumentException.ThrowIfNullOrEmpty(functionId);
        var action = $"get function {functionId} of app {appId}";
        var reply = await SendAsync(HttpMethod.Get, $"/v2/apps/{Escape(appId)}/functions/{Escape(functionId)}", null, _json.V2ReplyV2Function, action, cancellationToken).ConfigureAwait(false);
        return reply.Data is { Id: { Length: > 0 } } function ? ToFunction(function, appId) : throw NoData(action);
    }

    public async Task<AppSync> GetAppSyncAsync(string appId, string syncId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(appId);
        ArgumentException.ThrowIfNullOrEmpty(syncId);
        var action = $"get sync {syncId} of app {appId}";
        var reply = await SendAsync(HttpMethod.Get, $"/v2/apps/{Escape(appId)}/syncs/{Escape(syncId)}", null, _json.V2ReplyV2Sync, action, cancellationToken).ConfigureAwait(false);
        return reply.Data is { Id: { Length: > 0 } } sync ? ToSync(sync, appId) : throw NoData(action);
    }

    public async Task<AppSync> SyncAppAsync(string appId, string url, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(appId);
        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed) || parsed.Scheme is not ("http" or "https") || parsed.Host.Length == 0)
        {
            throw new ArgumentException("The sync URL must be an absolute http or https URL.", nameof(url));
        }

        var body = JsonSerializer.SerializeToUtf8Bytes(new V2SyncRequest { Url = url }, _json.V2SyncRequest);
        var action = $"sync app {appId}";
        var reply = await SendAsync(HttpMethod.Post, $"/v2/apps/{Escape(appId)}/syncs", body, _json.V2ReplyV2Sync, action, cancellationToken).ConfigureAwait(false);
        return reply.Data is { Id: { Length: > 0 } } sync ? ToSync(sync, appId) : throw NoData(action);
    }

    public async Task<RunSummary> GetRunAsync(string runId, bool includeOutput = false, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(runId);
        var action = $"get run {runId}";
        var reply = await SendAsync(HttpMethod.Get, $"/v2/runs/{Escape(runId)}{Included("output", includeOutput)}", null, _json.V2ReplyV2Run, action, cancellationToken).ConfigureAwait(false);
        return reply.Data is { Id: { Length: > 0 } } run ? ToRun(run) : throw NoData(action);
    }

    public IAsyncEnumerable<RunSummary> ListEventRunsAsync(string eventId, bool includeOutput = false, int? pageSize = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(eventId);
        return EachPageAsync(
            $"list the runs of event {eventId}",
            _json.V2ReplyV2RunArray,
            cursor => $"/v2/events/{Escape(eventId)}/runs" + Page(cursor, pageSize, includeOutput ? ("include", "output") : default),
            ToRun,
            cancellationToken);
    }

    public IAsyncEnumerable<RunSummary> ListFunctionRunsAsync(string appId, string functionId, RunQuery? query = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(appId);
        ArgumentException.ThrowIfNullOrEmpty(functionId);
        query ??= new RunQuery();
        var statuses = query.Statuses?.Select(ToStatusFilter).ToArray() ?? [];
        return EachPageAsync(
            $"list the runs of function {functionId} of app {appId}",
            _json.V2ReplyV2RunArray,
            cursor => $"/v2/apps/{Escape(appId)}/functions/{Escape(functionId)}/runs" + RunFilters(query, statuses, cursor, apps: false),
            ToRun,
            cancellationToken);
    }

    public async Task CancelRunAsync(string runId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(runId);
        _ = await SendAsync(HttpMethod.Post, $"/v2/runs/{Escape(runId)}/cancel", EmptyObject, _json.V2ReplyV2RunId, $"cancel run {runId}", cancellationToken).ConfigureAwait(false);
    }

    public async Task<string> RerunAsync(string runId, RerunRequest? request = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(runId);
        if (request is { } rerun && rerun.Input.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Array))
        {
            throw new ArgumentException("The step input must be a JSON array.", nameof(request));
        }

        var fromStep = request is null || (request.StepId is null && request.Input.ValueKind is JsonValueKind.Undefined)
            ? null
            : new V2RerunFromStep { StepId = request.StepId, Input = request.Input };
        var body = JsonSerializer.SerializeToUtf8Bytes(new V2RerunRequest { FromStep = fromStep }, _json.V2RerunRequest);
        var reply = await SendAsync(HttpMethod.Post, $"/v2/runs/{Escape(runId)}/rerun", body, _json.V2ReplyV2RunId, $"rerun {runId}", cancellationToken).ConfigureAwait(false);
        return reply.Data?.RunId is { Length: > 0 } id ? id : throw NoData($"rerun {runId}");
    }

    public async Task<ReplayInfo> CreateReplayAsync(string appId, string functionId, ReplayRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(appId);
        ArgumentException.ThrowIfNullOrEmpty(functionId);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrEmpty(request.Name);
        if (request.Name.Length is < 3 or > 64)
        {
            throw new ArgumentException("A replay name must be between 3 and 64 characters.", nameof(request));
        }

        if (request.Statuses is not { Count: > 0 and <= 5 })
        {
            throw new ArgumentException("A replay needs between 1 and 5 statuses.", nameof(request));
        }

        if (request.Statuses.Contains(ReplayStatus.All) && request.Statuses.Any(status => status != ReplayStatus.All))
        {
            throw new ArgumentException("ALL cannot be combined with another replay status.", nameof(request));
        }

        if (request.From > request.Until)
        {
            throw new ArgumentException("From must not be after Until.", nameof(request));
        }

        var body = JsonSerializer.SerializeToUtf8Bytes(
            new V2ReplayRequest
            {
                Name = request.Name,
                From = request.From.UtcDateTime,
                Until = request.Until.UtcDateTime,
                Statuses = [.. request.Statuses.Select(ToReplayStatus)],
            },
            _json.V2ReplayRequest);
        var action = $"replay function {functionId} of app {appId}";
        var reply = await SendAsync(HttpMethod.Post, $"/v2/apps/{Escape(appId)}/functions/{Escape(functionId)}/replays", body, _json.V2ReplyV2Replay, action, cancellationToken).ConfigureAwait(false);
        return reply.Data is { Id: { Length: > 0 } id } replay
            ? new ReplayInfo
            {
                Id = id,
                Name = replay.Name ?? request.Name,
                FunctionId = replay.FunctionId ?? functionId,
                From = replay.From,
                Until = replay.Until,
                Statuses = replay.Statuses?.Select(ParseReplayStatus).ToArray() ?? [],
                CreatedAt = replay.CreatedAt,
            }
            : throw NoData(action);
    }

    public async Task<FunctionInvocation> InvokeFunctionAsync(string appId, string functionId, InvokeRequest? request = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(appId);
        ArgumentException.ThrowIfNullOrEmpty(functionId);
        RequireObject(request?.Data ?? default, nameof(request));
        var body = JsonSerializer.SerializeToUtf8Bytes(
            new V2InvokeRequest { Data = request?.Data ?? default, IdempotencyKey = request?.IdempotencyKey },
            _json.V2InvokeRequest);
        var action = $"invoke function {functionId} of app {appId}";
        var reply = await SendAsync(HttpMethod.Post, $"/v2/apps/{Escape(appId)}/functions/{Escape(functionId)}/invoke", body, _json.V2ReplyV2Invoke, action, cancellationToken).ConfigureAwait(false);
        return reply.Data is { RunId: { Length: > 0 } runId } invoked
            ? new FunctionInvocation
            {
                RunId = runId,
                QueuedAt = invoked.QueuedAt,
                StartedAt = invoked.StartedAt,
                CompletedAt = invoked.CompletedAt,
                Result = string.IsNullOrEmpty(invoked.Result) ? null : invoked.Result,
                Error = string.IsNullOrEmpty(invoked.Error) ? null : invoked.Error,
            }
            : throw NoData(action);
    }

    public async Task<IReadOnlyList<RunScoreResult>> CreateScoresAsync(string runId, IReadOnlyList<RunScore> scores, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(runId);
        ArgumentNullException.ThrowIfNull(scores);
        if (scores.Count == 0)
        {
            throw new ArgumentException("At least one score is required.", nameof(scores));
        }

        var inputs = new V2ScoreInput[scores.Count];
        for (var index = 0; index < scores.Count; index++)
        {
            var score = scores[index] ?? throw new ArgumentException("A score is missing.", nameof(scores));
            ArgumentException.ThrowIfNullOrEmpty(score.Name);
            if (Encoding.UTF8.GetByteCount(score.Name) > MaxScoreNameBytes)
            {
                throw new ArgumentException("A score name must be at most 128 bytes.", nameof(scores));
            }

            RequireScore(score.Value, nameof(scores));
            inputs[index] = new V2ScoreInput
            {
                Name = score.Name,
                Value = score.Value,
                StepId = score.StepId,
                Experiment = score.ExperimentId is null && score.Variant is null ? null : new V2ScoreExperiment { Id = score.ExperimentId, Variant = score.Variant },
            };
        }

        var body = JsonSerializer.SerializeToUtf8Bytes(
            inputs,
            _json.V2ScoreInputArray);
        var action = $"score run {runId}";
        var reply = await SendAsync(HttpMethod.Post, $"/v2/runs/{Escape(runId)}/scores", body, _json.V2ReplyV2ScoreArray, action, cancellationToken).ConfigureAwait(false);
        return [.. (reply.Data ?? []).Select(score => new RunScoreResult
        {
            RunId = score.RunId ?? runId,
            Name = score.Name ?? string.Empty,
            Value = score.Value,
            StepId = score.StepId,
            ExperimentId = score.Experiment?.Id,
            Variant = score.Experiment?.Variant,
        })];
    }

    public async Task<FunctionTrace> GetRunTraceAsync(string runId, bool includeIO = false, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(runId);
        var action = $"get the trace of run {runId}";
        var reply = await SendAsync(HttpMethod.Get, $"/v2/runs/{Escape(runId)}/trace{Included("output", includeIO)}", null, _json.V2ReplyV2Trace, action, cancellationToken).ConfigureAwait(false);
        return reply.Data is { } trace
            ? new FunctionTrace { RunId = trace.RunId ?? runId, Root = trace.RootSpan is { } span ? ToSpan(span, depth: 0) : null }
            : throw NoData(action);
    }

    public async Task<AccountInfo> GetAccountAsync(CancellationToken cancellationToken = default)
    {
        var reply = await SendAsync(HttpMethod.Get, "/v2/account", null, _json.V2ReplyV2Account, "get the account", cancellationToken).ConfigureAwait(false);
        return reply.Data is { Id: { Length: > 0 } id } account
            ? new AccountInfo { Id = id, Name = account.Name ?? string.Empty, Email = account.Email ?? string.Empty, CreatedAt = account.CreatedAt, UpdatedAt = account.UpdatedAt }
            : throw NoData("get the account");
    }

    public async Task<string> SendEventAsync(EventDraft draft, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentException.ThrowIfNullOrEmpty(draft.Name);
        RequireObject(draft.Data, nameof(draft));
        RequireObject(draft.User, nameof(draft));
        var body = JsonSerializer.SerializeToUtf8Bytes(
            new V2EventWrite
            {
                Name = draft.Name,
                Data = draft.Data,
                Id = draft.Id,
                Ts = draft.Timestamp?.ToUnixTimeMilliseconds(),
                User = draft.User,
            },
            _json.V2EventWrite);
        var reply = await SendAsync(HttpMethod.Post, "/v2/events", body, _json.V2ReplyV2EventId, "send an event", cancellationToken).ConfigureAwait(false);
        return reply.Data?.EventId is { Length: > 0 } id ? id : throw NoData("send an event");
    }

    public IAsyncEnumerable<ExperimentInfo> ListExperimentsAsync(ExperimentQuery? query = null, CancellationToken cancellationToken = default)
    {
        query ??= new ExperimentQuery();
        RequirePageSize(query.PageSize);
        RequireRange(query.From, query.Until);
        return EachPageAsync(
            "list experiments",
            _json.V2ReplyV2ExperimentArray,
            cursor => "/v2/experiments" + ExperimentFilters(query, cursor, appAndFunction: true),
            experiment => ToExperiment(experiment),
            cancellationToken);
    }

    public IAsyncEnumerable<ExperimentInfo> ListFunctionExperimentsAsync(string appId, string functionId, ExperimentQuery? query = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(appId);
        ArgumentException.ThrowIfNullOrEmpty(functionId);
        query ??= new ExperimentQuery();
        RequirePageSize(query.PageSize);
        RequireRange(query.From, query.Until);
        return EachPageAsync(
            $"list the experiments of function {functionId} of app {appId}",
            _json.V2ReplyV2ExperimentArray,
            cursor => $"/v2/apps/{Escape(appId)}/functions/{Escape(functionId)}/experiments" + ExperimentFilters(query, cursor, appAndFunction: false),
            experiment => ToExperiment(experiment, appId, functionId),
            cancellationToken);
    }

    public async Task<ExperimentDetail> GetExperimentAsync(string appId, string functionId, string experimentId, string? variant = null, DateTimeOffset? from = null, DateTimeOffset? until = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(appId);
        ArgumentException.ThrowIfNullOrEmpty(functionId);
        ArgumentException.ThrowIfNullOrEmpty(experimentId);
        RequireRange(from, until);
        var parameters = new QueryString();
        parameters.Add("variant", variant);
        parameters.Add("from", from);
        parameters.Add("until", until);
        var action = $"get experiment {experimentId}";
        var reply = await SendAsync(
            HttpMethod.Get,
            $"/v2/apps/{Escape(appId)}/functions/{Escape(functionId)}/experiments/{Escape(experimentId)}{parameters}",
            null,
            _json.V2ReplyV2ExperimentDetail,
            action,
            cancellationToken).ConfigureAwait(false);
        return reply.Data is { Id: { Length: > 0 } id } experiment
            ? new ExperimentDetail
            {
                Id = id,
                SelectionStrategy = experiment.SelectionStrategy,
                FirstSeen = experiment.FirstSeen,
                LastSeen = experiment.LastSeen,
                Weights = experiment.VariantWeights?.Select(weight => new ExperimentWeight(weight.VariantName ?? string.Empty, weight.Weight)).ToArray() ?? [],
                Variants = experiment.Variants?.Select(item => new ExperimentVariant
                {
                    Name = item.VariantName ?? string.Empty,
                    RunCount = item.RunCount,
                    Metrics = item.Metrics?.Select(metric => new ExperimentMetric { Key = metric.Key ?? string.Empty, Average = metric.Avg, Min = metric.Min, Max = metric.Max }).ToArray() ?? [],
                }).ToArray() ?? [],
            }
            : throw NoData(action);
    }

    public IAsyncEnumerable<InsightsEventSchema> ListInsightsSchemasAsync(int? pageSize = null, CancellationToken cancellationToken = default) =>
        EachPageAsync(
            "list Insights event schemas",
            _json.V2ReplyV2InsightsSchemaArray,
            cursor => "/v2/insights/events/schemas" + Page(cursor, pageSize),
            schema => new InsightsEventSchema { Name = schema.Name ?? string.Empty, Schema = schema.Schema },
            cancellationToken);

    public async Task<InsightsResult> QueryInsightsAsync(string query, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(query);
        var body = JsonSerializer.SerializeToUtf8Bytes(new V2QueryRequest { Query = query }, _json.V2QueryRequest);
        var reply = await SendAsync(HttpMethod.Post, "/v2/insights/query", body, _json.V2ReplyV2InsightsQuery, "query Insights", cancellationToken).ConfigureAwait(false);
        var data = reply.Data ?? throw NoData("query Insights");
        return new InsightsResult
        {
            Columns = data.Columns?.Select(column => new InsightsResultColumn { Name = column.Name ?? string.Empty, Type = column.Type ?? string.Empty }).ToArray() ?? [],
            Rows = data.Rows?.Select(row => (IReadOnlyList<JsonElement>)(row.Values ?? [])).ToArray() ?? [],
            Diagnostics = data.Diagnostics?.Select(diagnostic => new InsightsDiagnostic { Code = diagnostic.Code, Message = diagnostic.Message ?? string.Empty, Severity = diagnostic.Severity }).ToArray() ?? [],
            Limited = data.Info?.Limited ?? false,
            PrimaryTable = data.Info?.PrimaryTable,
            Tables = data.Info?.Tables ?? [],
        };
    }

    public async Task<InsightsQuery> CreateInsightsQueryAsync(string prompt, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(prompt);
        var body = JsonSerializer.SerializeToUtf8Bytes(new V2PromptRequest { Prompt = prompt }, _json.V2PromptRequest);
        var reply = await SendAsync(HttpMethod.Post, "/v2/insights/query/prompt", body, _json.V2ReplyV2InsightsPrompt, "write an Insights query", cancellationToken).ConfigureAwait(false);
        return reply.Data is { Sql: { Length: > 0 } sql } query
            ? new InsightsQuery { Sql = sql, Summary = query.Summary }
            : throw NoData("write an Insights query");
    }

    public async Task<IReadOnlyList<InsightsTable>> ListInsightsTablesAsync(CancellationToken cancellationToken = default)
    {
        var reply = await SendAsync(HttpMethod.Get, "/v2/insights/tables", null, _json.V2ReplyV2InsightsTableArray, "list Insights tables", cancellationToken).ConfigureAwait(false);
        return [.. (reply.Data ?? []).Select(table => new InsightsTable
        {
            Name = table.Name ?? string.Empty,
            Description = table.Description,
            Columns = table.Columns?.Select(column => new InsightsColumn { Name = column.Name ?? string.Empty, Type = column.Type, Description = column.Description }).ToArray() ?? [],
        })];
    }

    public IAsyncEnumerable<EnvironmentInfo> ListEnvironmentsAsync(int? pageSize = null, CancellationToken cancellationToken = default) =>
        EachPageAsync("list environments", _json.V2ReplyV2EnvArray, cursor => "/v2/envs" + Page(cursor, pageSize), ToEnvironment, cancellationToken);

    public async Task<EnvironmentInfo> CreateEnvironmentAsync(string name, string? id = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        if (id is { Length: 0 })
        {
            throw new ArgumentException("An environment id cannot be empty.", nameof(id));
        }

        var body = JsonSerializer.SerializeToUtf8Bytes(new V2EnvCreate { Name = name, Id = id }, _json.V2EnvCreate);
        var reply = await SendAsync(HttpMethod.Post, "/v2/envs", body, _json.V2ReplyV2Env, "create an environment", cancellationToken).ConfigureAwait(false);
        return reply.Data is { Id: { Length: > 0 } } environment ? ToEnvironment(environment) : throw NoData("create an environment");
    }

    public async Task<EnvironmentInfo> UpdateEnvironmentAsync(string id, bool archived, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        var body = JsonSerializer.SerializeToUtf8Bytes(new V2EnvPatch { IsArchived = archived }, _json.V2EnvPatch);
        var reply = await SendAsync(HttpMethod.Patch, $"/v2/envs/{Escape(id)}", body, _json.V2ReplyV2Env, $"update environment {id}", cancellationToken).ConfigureAwait(false);
        return reply.Data is { Id: { Length: > 0 } } environment ? ToEnvironment(environment) : throw NoData($"update environment {id}");
    }

    public IAsyncEnumerable<WebhookInfo> ListWebhooksAsync(int? pageSize = null, CancellationToken cancellationToken = default)
    {
        RequireWebhookEnvironment();
        return EachPageAsync("list webhooks", _json.V2ReplyV2WebhookArray, cursor => "/v2/env/webhooks" + Page(cursor, pageSize), ToWebhook, cancellationToken);
    }

    public async Task<WebhookInfo> CreateWebhookAsync(WebhookRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrEmpty(request.Name);
        RequireWebhookEnvironment();
        var body = JsonSerializer.SerializeToUtf8Bytes(
            new V2WebhookCreate
            {
                Name = request.Name,
                Transform = request.Transform,
                Response = request.Response,
                EventFilter = EventFilter(request),
            },
            _json.V2WebhookCreate);
        var reply = await SendAsync(HttpMethod.Post, "/v2/env/webhooks", body, _json.V2ReplyV2Webhook, "create a webhook", cancellationToken).ConfigureAwait(false);
        return reply.Data is { Id: { Length: > 0 } } webhook ? ToWebhook(webhook) : throw NoData("create a webhook");
    }

    public IAsyncEnumerable<SessionKeyInfo> ListSessionKeysAsync(string? search = null, int? pageSize = null, CancellationToken cancellationToken = default) =>
        EachPageAsync(
            "list session keys",
            _json.V2ReplyV2SessionKeyArray,
            cursor => "/v2/sessions" + Page(cursor, pageSize, ("search", string.IsNullOrWhiteSpace(search) ? null : search)),
            key => new SessionKeyInfo { Id = key.Id ?? string.Empty, CreatedAt = key.CreatedAt },
            cancellationToken);

    public IAsyncEnumerable<SessionInfo> ListSessionsAsync(string sessionKey, SessionQuery? query = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(sessionKey);
        query ??= new SessionQuery();
        RequirePageSize(query.PageSize);
        RequireRange(query.From, query.Until);
        return EachPageAsync(
            $"list the sessions of {sessionKey}",
            _json.V2ReplyV2SessionArray,
            cursor =>
            {
                var parameters = new QueryString();
                parameters.Add("cursor", cursor);
                parameters.Add("limit", query.PageSize);
                parameters.Add("search", string.IsNullOrWhiteSpace(query.Search) ? null : query.Search);
                parameters.Add("from", query.From);
                parameters.Add("until", query.Until);
                return $"/v2/sessions/{Escape(sessionKey)}{parameters}";
            },
            ToSession,
            cancellationToken);
    }

    public IAsyncEnumerable<SessionRun> ListSessionRunsAsync(string sessionKey, string sessionId, int? pageSize = null, DateTimeOffset? from = null, DateTimeOffset? until = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(sessionKey);
        ArgumentException.ThrowIfNullOrEmpty(sessionId);
        RequirePageSize(pageSize);
        RequireRange(from, until);
        return EachPageAsync(
            $"list the runs of session {sessionId}",
            _json.V2ReplyV2SessionRunArray,
            cursor =>
            {
                var parameters = new QueryString();
                parameters.Add("cursor", cursor);
                parameters.Add("limit", pageSize);
                parameters.Add("from", from);
                parameters.Add("until", until);
                return $"/v2/sessions/{Escape(sessionKey)}/{Escape(sessionId)}/runs{parameters}";
            },
            run => new SessionRun
            {
                Id = run.Id ?? string.Empty,
                Status = ParseStatus(run.Status),
                FunctionId = run.Function?.Id,
                FunctionName = run.Function?.Name,
                EventName = run.EventName,
                QueuedAt = run.QueuedAt,
                StartedAt = run.StartedAt,
                EndedAt = run.EndedAt,
            },
            cancellationToken);
    }

    private async IAsyncEnumerable<TItem> EachPageAsync<TWire, TItem>(
        string action,
        JsonTypeInfo<V2Reply<TWire[]>> typeInfo,
        Func<string?, string> path,
        Func<TWire, TItem> map,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        string? cursor = null;
        while (true)
        {
            var reply = await SendAsync(HttpMethod.Get, path(cursor), body: null, typeInfo, action, cancellationToken).ConfigureAwait(false);
            StopIfCursorRepeats(cursor, reply.Page, action);

            foreach (var item in reply.Data ?? [])
            {
                yield return map(item);
            }

            if (reply.Page is not { HasMore: true, Cursor: { Length: > 0 } nextCursor })
            {
                yield break;
            }

            cursor = nextCursor;
        }
    }

    private static string Page(string? cursor, int? pageSize, params (string Name, string? Value)[] extra)
    {
        RequirePageSize(pageSize);
        var parameters = new QueryString();
        parameters.Add("cursor", cursor);
        parameters.Add("limit", pageSize);
        foreach (var (name, value) in extra)
        {
            parameters.Add(name, value);
        }

        return parameters.ToString();
    }

    private static string Included(string value, bool include) => include ? $"?include={value}" : string.Empty;

    private static string RunFilters(RunQuery query, string[] statuses, string? cursor, bool apps)
    {
        RequirePageSize(query.PageSize);
        RequireRange(query.From, query.Until);
        var parameters = new QueryString();
        parameters.Add("cursor", cursor);
        parameters.Add("limit", query.PageSize);
        parameters.Add("from", query.From);
        parameters.Add("until", query.Until);
        if (query.TimeField != RunTimeField.QueuedAt)
        {
            parameters.Add("timeField", query.TimeField == RunTimeField.StartedAt ? "startedAt" : "endedAt");
        }

        foreach (var status in statuses)
        {
            parameters.Add("status", status);
        }

        if (apps)
        {
            foreach (var appId in query.AppIds ?? [])
            {
                parameters.Add("appId", appId);
            }

            foreach (var functionId in query.FunctionIds ?? [])
            {
                parameters.Add("functionId", functionId);
            }
        }

        if (query.Deferred is { } deferred)
        {
            parameters.Add("isDeferred", deferred ? "true" : "false");
        }

        parameters.Add("query", string.IsNullOrWhiteSpace(query.Query) ? null : query.Query);
        if (query.Ascending)
        {
            parameters.Add("order", "ASC");
        }

        if (query.IncludeOutput)
        {
            parameters.Add("include", "output");
        }

        if (query.IncludeDeferredFrom)
        {
            parameters.Add("include", "deferredFrom");
        }

        return parameters.ToString();
    }

    private static string ExperimentFilters(ExperimentQuery query, string? cursor, bool appAndFunction)
    {
        var parameters = new QueryString();
        parameters.Add("cursor", cursor);
        parameters.Add("limit", query.PageSize);
        if (appAndFunction)
        {
            parameters.Add("appId", query.AppId);
            parameters.Add("functionId", query.FunctionId);
        }

        parameters.Add("from", query.From);
        parameters.Add("until", query.Until);
        return parameters.ToString();
    }

    private static AppInfo ToApp(V2App app) => new()
    {
        Id = app.Id ?? string.Empty,
        Name = app.Name ?? string.Empty,
        AppVersion = app.AppVersion,
        FunctionCount = app.FunctionCount,
        IsArchived = app.IsArchived,
        CreatedAt = app.CreatedAt,
        ArchivedAt = app.ArchivedAt,
        Method = app.Method,
        LatestSync = app.LatestSync is { } sync
            ? new AppLatestSync
            {
                Status = sync.Status ?? string.Empty,
                Url = sync.Url,
                SdkLanguage = sync.SdkLanguage,
                SdkVersion = sync.SdkVersion,
                Framework = sync.Framework,
                AppVersion = sync.AppVersion,
                SyncedAt = sync.SyncedAt,
                Error = sync.Error,
            }
            : null,
    };

    private static AppSync ToSync(V2Sync sync, string appId) => new()
    {
        Id = sync.Id ?? string.Empty,
        AppId = sync.AppId ?? appId,
        Status = sync.Status ?? string.Empty,
        Url = sync.Url,
        SdkLanguage = sync.SdkLanguage,
        SdkVersion = sync.SdkVersion,
        Framework = sync.Framework,
        AppVersion = sync.AppVersion,
        Error = ToError(sync.Error),
        CreatedAt = sync.CreatedAt,
    };

    private static ExperimentInfo ToExperiment(V2Experiment experiment, string? appId = null, string? functionId = null) => new()
    {
        Id = experiment.Id ?? string.Empty,
        AppId = experiment.Function?.App?.Id ?? appId,
        FunctionId = experiment.Function?.Id ?? functionId,
        FunctionName = experiment.Function?.Name,
        SelectionStrategy = experiment.SelectionStrategy,
        TotalRuns = experiment.TotalRuns,
        VariantCount = experiment.VariantCount,
        Variants = experiment.Variants ?? [],
        FirstSeen = experiment.FirstSeen,
        LastSeen = experiment.LastSeen,
    };

    private static EnvironmentInfo ToEnvironment(V2Env environment) => new()
    {
        Id = environment.Id ?? string.Empty,
        Name = environment.Name ?? string.Empty,
        Type = environment.Type ?? string.Empty,
        IsArchived = environment.IsArchived,
        CreatedAt = environment.CreatedAt,
    };

    private static WebhookInfo ToWebhook(V2Webhook webhook) => new()
    {
        Id = webhook.Id ?? string.Empty,
        Name = webhook.Name ?? string.Empty,
        Url = webhook.Url,
        Environment = webhook.Environment,
        Transform = webhook.Transform,
        Response = webhook.Response,
        Events = webhook.EventFilter is { } filter
            ? new WebhookEventFilter { Names = filter.Events ?? [], Filter = filter.Filter }
            : null,
        CreatedAt = webhook.CreatedAt,
        UpdatedAt = webhook.UpdatedAt,
    };

    private static SessionInfo ToSession(V2Session session) => new()
    {
        Id = session.Id ?? string.Empty,
        RunCount = session.RunCount,
        FailedRunCount = session.FailedRunCount,
        FailureRate = session.FailureRate,
        LastActiveAt = session.LastActiveAt,
        Functions = session.Functions?.Select(function => new SessionFunction(function.Id ?? string.Empty, function.Name)).ToArray() ?? [],
    };

    private static FunctionTraceSpan ToSpan(V2TraceSpan span, int depth)
    {
        if (depth > MaxSpanDepth)
        {
            throw new InngestException("Failed to read the trace: it is nested more deeply than the client reads.");
        }

        return new FunctionTraceSpan
        {
            Id = span.Id ?? string.Empty,
            Name = span.Name ?? string.Empty,
            Status = span.Status ?? string.Empty,
            StepId = span.StepId,
            StepOp = span.StepOp,
            StepType = span.StepType,
            QueuedAt = span.QueuedAt,
            StartedAt = span.StartedAt,
            EndedAt = span.EndedAt,
            DurationMs = Duration(span.DurationMs),
            Input = span.Input,
            Output = span.Output,
            Children = span.Children?.Select(child => ToSpan(child, depth + 1)).ToArray() ?? [],
        };
    }

    private void RequireWebhookEnvironment()
    {
        if (_transport.Settings.IsDev || !string.IsNullOrEmpty(_transport.Settings.Environment))
        {
            return;
        }

        throw new ArgumentException("Webhooks need an environment. Set InngestOptions.Environment or INNGEST_ENV.");
    }

    private static V2EventFilter? EventFilter(WebhookRequest request)
    {
        if (request.Events is not { } events)
        {
            return null;
        }

        if (events.Filter is not null && events.Filter is not ("ALLOW" or "DENY"))
        {
            throw new ArgumentException("A webhook event filter must be ALLOW or DENY.", nameof(request));
        }

        if (events.Names is { } names)
        {
            foreach (var name in names)
            {
                if (string.IsNullOrEmpty(name))
                {
                    throw new ArgumentException("A webhook event name is missing.", nameof(request));
                }
            }
        }

        if (events.Names is not { Count: > 0 } && events.Filter is null)
        {
            return null;
        }

        return new V2EventFilter
        {
            Events = events.Names is { Count: > 0 } ? [.. events.Names] : null,
            Filter = events.Filter,
        };
    }

    private static void RequireObject(JsonElement value, string name)
    {
        if (value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Object)
        {
            return;
        }

        throw new ArgumentException("The JSON value must be an object.", name);
    }

    private static void RequireScore(JsonElement value, string name)
    {
        if (value.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            return;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) && double.IsFinite(number))
        {
            return;
        }

        throw new ArgumentException("A score value must be a finite number or a boolean.", name);
    }
}
