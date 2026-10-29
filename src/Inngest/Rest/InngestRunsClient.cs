// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Inngest.Client;
using Inngest.Internal;
using Microsoft.Extensions.Logging;

namespace Inngest.Rest;

internal sealed class InngestRunsClient : IInngestRunsClient
{
    private readonly InngestApiTransport _transport;
    private readonly JsonSerializerOptions _serializerOptions;

    public InngestRunsClient(IHttpClientFactory httpClientFactory, InngestSettings settings, ILogger<InngestRunsClient> logger)
    {
        _transport = new InngestApiTransport(httpClientFactory, settings, logger);
        _serializerOptions = settings.SerializerOptions;
    }

    public async Task<FunctionRun> GetRunAsync(string runId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(runId);

        var action = $"get run {runId}";
        var reply = await GetAsync($"/v1/runs/{Uri.EscapeDataString(runId)}", RestJsonContext.Default.RunReply, action, cancellationToken)
            .ConfigureAwait(false);
        return reply.Data is { } run ? ToRun(run) : throw new InngestException($"Failed to {action}: Inngest's reply has no run.");
    }

    public async Task<IReadOnlyList<FunctionRun>> GetEventRunsAsync(string eventId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(eventId);

        var reply = await GetAsync(
            $"/v1/events/{Uri.EscapeDataString(eventId)}/runs",
            RestJsonContext.Default.RunListReply,
            $"list the runs of event {eventId}",
            cancellationToken).ConfigureAwait(false);
        return reply.Data is { Length: > 0 } runs ? Array.ConvertAll(runs, ToRun) : [];
    }

    public async Task CancelRunAsync(string runId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(runId);

        // Inngest answers 200 or 204, with nothing in the body that is needed.
        using var response = await SendAsync(HttpMethod.Delete, $"/v1/runs/{Uri.EscapeDataString(runId)}", $"cancel run {runId}", cancellationToken)
            .ConfigureAwait(false);
    }

    private static FunctionRunStatus ParseStatus(string? status) => status switch
    {
        "Running" => FunctionRunStatus.Running,
        "Completed" => FunctionRunStatus.Completed,
        "Failed" => FunctionRunStatus.Failed,
        "Cancelled" => FunctionRunStatus.Cancelled,
        "Scheduled" => FunctionRunStatus.Scheduled,
        _ => FunctionRunStatus.Unknown,
    };

    private FunctionRun ToRun(RunData run) => new()
    {
        RunId = run.RunId ?? string.Empty,
        Status = ParseStatus(run.Status),
        FunctionId = run.FunctionId ?? string.Empty,
        FunctionVersion = run.FunctionVersion,
        StartedAt = run.StartedAt,
        EndedAt = run.EndedAt,
        EventId = run.EventId,
        BatchId = run.BatchId,
        OriginalRunId = run.OriginalRunId,
        Cron = run.Cron,
        EnvironmentId = run.EnvironmentId,
        Output = run.Output,
        SerializerOptions = _serializerOptions,
    };

    private async Task<T> GetAsync<T>(string path, JsonTypeInfo<T> typeInfo, string action, CancellationToken cancellationToken)
        where T : class
    {
        using var response = await SendAsync(HttpMethod.Get, path, action, cancellationToken).ConfigureAwait(false);
        try
        {
            await using var body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            return await JsonSerializer.DeserializeAsync(body, typeInfo, cancellationToken).ConfigureAwait(false)
                ?? throw new InngestException($"Failed to {action}: Inngest's reply is empty.");
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
    }

    // Returns a successful response with its body unread, or throws with the reason Inngest gave.
    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string action, CancellationToken cancellationToken)
    {
        HttpResponseMessage? response = null;
        try
        {
            response = await _transport.SendAsync(new ApiCall(method, _transport.Url(path)), HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                return response;
            }

            var status = (int)response.StatusCode;
            var content = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            throw new InngestException($"Failed to {action}: {ApiHttp.DescribeError(status, content)}", status);
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException)
        {
            response?.Dispose();
            throw new InngestException($"Failed to {action}: {exception.Message}", exception);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            response?.Dispose();
            throw new InngestException($"Failed to {action}: the request to Inngest timed out.", exception);
        }
        catch
        {
            response?.Dispose();
            throw;
        }
    }
}
