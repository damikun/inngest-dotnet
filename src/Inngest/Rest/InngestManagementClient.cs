// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Inngest.Client;
using Inngest.Configuration;
using Inngest.Json;
using Microsoft.Extensions.Logging;

namespace Inngest.Rest;

internal sealed class InngestManagementClient : IInngestManagementClient
{
    private const int MaxFunctionIds = 500;

    private readonly InngestApiTransport _transport;

    // Outputs and payloads are any JSON a function returned or an event carried, so replies are read with the depth the
    // app's serializer allows.
    private readonly ManagementJsonContext _json;

    public InngestManagementClient(IHttpClientFactory httpClientFactory, InngestSettings settings, ILogger<InngestManagementClient> logger)
    {
        _transport = new InngestApiTransport(httpClientFactory, settings, logger);
        _json = new ManagementJsonContext(new JsonSerializerOptions(ManagementJsonContext.Default.Options)
        {
            MaxDepth = JsonPayload.DocumentOptions(settings.SerializerOptions).MaxDepth,
        });
    }

    public Task<FunctionInfo> PauseFunctionAsync(string appId, string functionId, CancellationToken cancellationToken = default) =>
        ChangeFunctionAsync(appId, functionId, "pause", cancellationToken);

    public Task<FunctionInfo> ResumeFunctionAsync(string appId, string functionId, CancellationToken cancellationToken = default) =>
        ChangeFunctionAsync(appId, functionId, "resume", cancellationToken);

    public Task<IReadOnlyList<FunctionPauseResult>> PauseFunctionsAsync(string appId, IReadOnlyList<string> functionIds, CancellationToken cancellationToken = default) =>
        ChangeFunctionsAsync(appId, functionIds, "pause", cancellationToken);

    public Task<IReadOnlyList<FunctionPauseResult>> ResumeFunctionsAsync(string appId, IReadOnlyList<string> functionIds, CancellationToken cancellationToken = default) =>
        ChangeFunctionsAsync(appId, functionIds, "resume", cancellationToken);

    public async Task<RunCancellation> CancelRunsAsync(string appId, string functionId, CancelRunsRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(appId);
        ArgumentException.ThrowIfNullOrEmpty(functionId);
        ArgumentNullException.ThrowIfNull(request);
        if (request.From > request.Until)
        {
            throw new ArgumentException("From must not be after Until.", nameof(request));
        }

        var body = JsonSerializer.SerializeToUtf8Bytes(
            new V2CancellationRequest { From = request.From.UtcDateTime, Until = request.Until.UtcDateTime, If = request.If, Name = request.Name },
            _json.V2CancellationRequest);
        var reply = await SendAsync(
            HttpMethod.Post,
            $"/v2/apps/{Escape(appId)}/functions/{Escape(functionId)}/cancellations",
            body,
            _json.V2ReplyV2Cancellation,
            $"cancel the runs of function {functionId} of app {appId}",
            cancellationToken).ConfigureAwait(false);
        return reply.Data is { Id: { } id } data
            ? new RunCancellation
            {
                Id = id,
                AppId = data.AppId ?? appId,
                FunctionId = data.FunctionId ?? functionId,
                Name = data.Name,
                From = data.From,
                Until = data.Until,
                If = data.If,
                CreatedAt = data.CreatedAt,
            }
            : throw NoData("cancel the runs of function " + functionId);
    }

    public async IAsyncEnumerable<EventInfo> ListEventsAsync(EventQuery? query = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        query ??= new EventQuery();
        var from = query.From;
        var until = query.Until;
        string? cursor = null;
        while (true)
        {
            var parameters = new QueryString();
            parameters.Add("cursor", cursor);
            parameters.Add("limit", query.PageSize);
            parameters.Add("from", from);
            parameters.Add("until", until);
            foreach (var name in query.Names ?? [])
            {
                parameters.Add("name", name);
            }

            if (query.IncludePayload)
            {
                parameters.Add("include", "payload");
            }

            var reply = await SendAsync(HttpMethod.Get, "/v2/events" + parameters, body: null, _json.V2ReplyV2EventArray, "list events", cancellationToken)
                .ConfigureAwait(false);
            foreach (var data in reply.Data ?? [])
            {
                yield return ToEvent(data);
            }

            // Later pages keep the range of the first, which Inngest fills in when the query leaves it open.
            from ??= reply.Metadata?.TimeRange?.From;
            until ??= reply.Metadata?.TimeRange?.Until;
            if (reply.Page is not { HasMore: true, Cursor: { Length: > 0 } next })
            {
                yield break;
            }

            cursor = next;
        }
    }

    public async Task<EventInfo> GetEventAsync(string eventId, bool includePayload = false, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(eventId);

        var reply = await SendAsync(
            HttpMethod.Get,
            $"/v2/events/{Escape(eventId)}{(includePayload ? "?include=payload" : string.Empty)}",
            body: null,
            _json.V2ReplyV2Event,
            $"get event {eventId}",
            cancellationToken).ConfigureAwait(false);
        return reply.Data is { Id: not null } data ? ToEvent(data) : throw NoData($"get event {eventId}");
    }

    public async IAsyncEnumerable<AppSync> ListAppSyncsAsync(string appId, int? pageSize = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(appId);

        string? cursor = null;
        while (true)
        {
            var parameters = new QueryString();
            parameters.Add("cursor", cursor);
            parameters.Add("limit", pageSize);
            var reply = await SendAsync(
                HttpMethod.Get,
                $"/v2/apps/{Escape(appId)}/syncs{parameters}",
                body: null,
                _json.V2ReplyV2SyncArray,
                $"list the syncs of app {appId}",
                cancellationToken).ConfigureAwait(false);
            foreach (var sync in reply.Data ?? [])
            {
                yield return new AppSync
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
            }

            if (reply.Page is not { HasMore: true, Cursor: { Length: > 0 } next })
            {
                yield break;
            }

            cursor = next;
        }
    }

    public async IAsyncEnumerable<RunSummary> ListRunsAsync(RunQuery? query = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        query ??= new RunQuery();
        if (query.FunctionIds is { Count: > 0 } && query.AppIds is not { Count: > 0 })
        {
            throw new ArgumentException("Filtering by FunctionIds needs AppIds too.", nameof(query));
        }

        var statuses = query.Statuses?.Select(ToStatusFilter).ToArray() ?? [];
        string? cursor = null;
        while (true)
        {
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

            foreach (var appId in query.AppIds ?? [])
            {
                parameters.Add("appId", appId);
            }

            foreach (var functionId in query.FunctionIds ?? [])
            {
                parameters.Add("functionId", functionId);
            }

            parameters.Add("query", query.Query);
            if (query.Ascending)
            {
                parameters.Add("order", "ASC");
            }

            if (query.IncludeOutput)
            {
                parameters.Add("include", "output");
            }

            var reply = await SendAsync(HttpMethod.Get, "/v2/runs" + parameters, body: null, _json.V2ReplyV2RunArray, "list runs", cancellationToken)
                .ConfigureAwait(false);
            foreach (var run in reply.Data ?? [])
            {
                yield return ToRun(run);
            }

            if (reply.Page is not { HasMore: true, Cursor: { Length: > 0 } next })
            {
                yield break;
            }

            cursor = next;
        }
    }

    private static string ToStatusFilter(FunctionRunStatus status) => status switch
    {
        FunctionRunStatus.Scheduled => "QUEUED",
        FunctionRunStatus.Running => "RUNNING",
        FunctionRunStatus.Paused => "PAUSED",
        FunctionRunStatus.Completed => "COMPLETED",
        FunctionRunStatus.Failed => "FAILED",
        FunctionRunStatus.Cancelled => "CANCELLED",
        FunctionRunStatus.Skipped => "SKIPPED",
        _ => throw new ArgumentException($"Runs cannot be listed by the status {status}.", nameof(status)),
    };

    private static FunctionRunStatus ParseStatus(string? status) => status switch
    {
        "QUEUED" => FunctionRunStatus.Scheduled,
        "RUNNING" => FunctionRunStatus.Running,
        "PAUSED" => FunctionRunStatus.Paused,
        "COMPLETED" => FunctionRunStatus.Completed,
        "FAILED" => FunctionRunStatus.Failed,
        "CANCELLED" => FunctionRunStatus.Cancelled,
        "SKIPPED" => FunctionRunStatus.Skipped,
        _ => FunctionRunStatus.Unknown,
    };

    private static RunSummary ToRun(V2Run run) => new()
    {
        RunId = run.Id ?? string.Empty,
        Status = ParseStatus(run.Status),
        FunctionId = run.Function?.Id ?? string.Empty,
        FunctionName = run.Function?.Name,
        AppId = run.App?.Id,
        QueuedAt = run.QueuedAt,
        StartedAt = run.StartedAt,
        EndedAt = run.EndedAt,
        EventIds = run.Trigger?.EventIds ?? [],
        EventName = run.Trigger?.EventName,
        BatchId = run.Trigger?.BatchId,
        Cron = run.Trigger?.CronSchedule,
        Output = run.Output,
    };

    private static EventInfo ToEvent(V2Event data) => new()
    {
        Id = data.Id ?? string.Empty,
        Name = data.Name ?? string.Empty,
        ReceivedAt = data.ReceivedAt,
        OccurredAt = data.OccurredAt,
        IdempotencyKey = string.IsNullOrEmpty(data.IdempotencyKey) ? null : data.IdempotencyKey,
        Version = string.IsNullOrEmpty(data.Version) ? null : data.Version,
        Payload = data.Payload,
    };

    private static FunctionInfo ToFunction(V2Function function, string appId) => new()
    {
        Id = function.Id ?? string.Empty,
        Name = function.Name ?? string.Empty,
        AppId = function.App?.Id ?? appId,
        IsPaused = function.IsPaused,
        IsArchived = function.IsArchived,
        Triggers = function.Triggers is { } triggers
            ? [.. triggers.Select(trigger => new FunctionTriggerInfo(trigger.Type ?? string.Empty, trigger.Value ?? string.Empty, trigger.If))]
            : [],
    };

    private static InngestApiError? ToError(V2Error? error) =>
        error is null ? null : new InngestApiError(error.Code ?? string.Empty, error.Message ?? string.Empty);

    private static string Escape(string segment) => Uri.EscapeDataString(segment);

    private static InngestException NoData(string action) => new($"Failed to {action}: Inngest's reply has no data.");

    private async Task<FunctionInfo> ChangeFunctionAsync(string appId, string functionId, string change, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(appId);
        ArgumentException.ThrowIfNullOrEmpty(functionId);

        // No body at all: Inngest refuses one on this endpoint.
        var reply = await SendAsync(
            HttpMethod.Post,
            $"/v2/apps/{Escape(appId)}/functions/{Escape(functionId)}/{change}",
            body: null,
            _json.V2ReplyV2Function,
            $"{change} function {functionId} of app {appId}",
            cancellationToken).ConfigureAwait(false);
        return reply.Data is { } function ? ToFunction(function, appId) : throw NoData($"{change} function {functionId}");
    }

    private async Task<IReadOnlyList<FunctionPauseResult>> ChangeFunctionsAsync(
        string appId,
        IReadOnlyList<string> functionIds,
        string change,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(appId);
        ArgumentNullException.ThrowIfNull(functionIds);
        if (functionIds.Count is 0 or > MaxFunctionIds)
        {
            throw new ArgumentException($"Between 1 and {MaxFunctionIds} function ids can be sent at once.", nameof(functionIds));
        }

        var body = JsonSerializer.SerializeToUtf8Bytes(new V2FunctionIds { FunctionIds = [.. functionIds] }, _json.V2FunctionIds);
        var reply = await SendAsync(
            HttpMethod.Post,
            $"/v2/apps/{Escape(appId)}/functions/{change}",
            body,
            _json.V2ReplyV2FunctionPauseResultArray,
            $"{change} {functionIds.Count} function(s) of app {appId}",
            cancellationToken).ConfigureAwait(false);
        return [.. (reply.Data ?? []).Select(result => new FunctionPauseResult
        {
            FunctionId = result.FunctionId ?? string.Empty,
            Function = result.Function is { } function ? ToFunction(function, appId) : null,
            Error = ToError(result.Error),
        })];
    }

    private async Task<V2Reply<T>> SendAsync<T>(
        HttpMethod method,
        string pathAndQuery,
        byte[]? body,
        JsonTypeInfo<V2Reply<T>> typeInfo,
        string action,
        CancellationToken cancellationToken)
    {
        HttpResponseMessage? response = null;
        try
        {
            response = await _transport.SendAsync(
                new ApiCall(method, _transport.Url(pathAndQuery), body is null ? null : new ReadOnlyMemory<byte>(body)),
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            var content = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var status = (int)response.StatusCode;
                throw new InngestException($"Failed to {action}: {DescribeError(status, content)}", status);
            }

            return JsonSerializer.Deserialize(content, typeInfo) ?? throw new InngestException($"Failed to {action}: Inngest's reply is empty.");
        }
        catch (JsonException exception)
        {
            throw new InngestException($"Failed to {action}: Inngest returned invalid JSON.", exception);
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException)
        {
            throw new InngestException($"Failed to {action}: {exception.Message}", exception);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new InngestException($"Failed to {action}: the request to Inngest timed out.", exception);
        }
        finally
        {
            response?.Dispose();
        }
    }

    // The v2 API answers {"errors":[{"code":"not_found","message":"Function not found"}]}; a self-hosted server that
    // refuses the key answers in plain text.
    private string DescribeError(int status, byte[] content)
    {
        try
        {
            if (JsonSerializer.Deserialize(content, _json.V2ErrorReply)?.Errors is { Length: > 0 } errors)
            {
                return string.Join("; ", errors.Select(static error => $"{error.Message} ({error.Code})"));
            }
        }
        catch (JsonException)
        {
        }

        return ApiHttp.DescribeError(status, content);
    }

    // Query parameters in the order they are added, with times in UTC as RFC 3339.
    private sealed class QueryString
    {
        private readonly StringBuilder _text = new();

        public void Add(string name, string? value)
        {
            if (value is null)
            {
                return;
            }

            _text.Append(_text.Length == 0 ? '?' : '&').Append(name).Append('=').Append(Uri.EscapeDataString(value));
        }

        public void Add(string name, int? value) => Add(name, value?.ToString(CultureInfo.InvariantCulture));

        public void Add(string name, DateTimeOffset? value) =>
            Add(name, value?.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'", CultureInfo.InvariantCulture));

        public override string ToString() => _text.ToString();
    }
}
