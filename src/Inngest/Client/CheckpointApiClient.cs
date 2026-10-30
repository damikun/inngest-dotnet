// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using System.Net;
using System.Text.Json;
using Inngest.Configuration;
using Inngest.Internal;
using Microsoft.Extensions.Logging;

namespace Inngest.Client;

// POST {api}/v1/checkpoint/{runId}/async, retried like inngest-js with up to 5 attempts and backoff from 100 ms. A
// 409 is never retried: Inngest has dispatched the run again, and the steps belong to that newer call.
internal sealed class CheckpointApiClient : ICheckpointApi
{
    internal const int MaxAttempts = 5;

    private static readonly JsonEncodedText RunIdProperty = JsonEncodedText.Encode("run_id");
    private static readonly JsonEncodedText FunctionIdProperty = JsonEncodedText.Encode("fn_id");
    private static readonly JsonEncodedText QueueItemIdProperty = JsonEncodedText.Encode("qi_id");
    private static readonly JsonEncodedText RequestIdProperty = JsonEncodedText.Encode("request_id");
    private static readonly JsonEncodedText GenerationIdProperty = JsonEncodedText.Encode("generation_id");
    private static readonly JsonEncodedText RequestStartedAtProperty = JsonEncodedText.Encode("request_started_at");
    private static readonly JsonEncodedText StepsProperty = JsonEncodedText.Encode("steps");
    private static readonly JsonEncodedText TimestampProperty = JsonEncodedText.Encode("ts");

    private readonly InngestApiTransport _transport;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<CheckpointApiClient> _logger;

    public CheckpointApiClient(
        IHttpClientFactory httpClientFactory,
        InngestSettings settings,
        TimeProvider timeProvider,
        ILogger<CheckpointApiClient> logger)
    {
        _transport = new InngestApiTransport(httpClientFactory, settings, logger);
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task CheckpointAsync(AsyncCheckpoint checkpoint, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(checkpoint.RunId);

        var url = _transport.Url($"/v1/checkpoint/{Uri.EscapeDataString(checkpoint.RunId)}/async");
        using var body = new PooledBufferWriter(checkpoint.Steps.Length + 512);
        for (var attempt = 1; ; attempt++)
        {
            // Rewritten for every attempt, so that "ts" says when Inngest was asked.
            body.Clear();
            WriteBody(body, checkpoint, _timeProvider.GetUtcNow());

            var (error, retryable) = await TrySendAsync(url, body.WrittenMemory, checkpoint.RunId, cancellationToken).ConfigureAwait(false);
            if (error is null)
            {
                return;
            }

            if (attempt == MaxAttempts || !retryable)
            {
                throw error;
            }

            var delay = InngestClient.GetBackoffDelay(attempt, Random.Shared.NextDouble());
            Log.RetryingCheckpoint(_logger, checkpoint.RunId, attempt, MaxAttempts, error.Message, (int)delay.TotalMilliseconds);
            await Task.Delay(delay, _timeProvider, cancellationToken).ConfigureAwait(false);
        }
    }

    // The order of inngest-js; ids Inngest did not send are left out.
    internal static void WriteBody(PooledBufferWriter body, AsyncCheckpoint checkpoint, DateTimeOffset now)
    {
        using var writer = new Utf8JsonWriter(body);
        writer.WriteStartObject();
        writer.WriteString(RunIdProperty, checkpoint.RunId);
        writer.WriteString(FunctionIdProperty, checkpoint.FunctionId);
        writer.WriteString(QueueItemIdProperty, checkpoint.QueueItemId);
        if (checkpoint.RequestId is { } requestId)
        {
            writer.WriteString(RequestIdProperty, requestId);
        }

        if (checkpoint.GenerationId is { } generationId)
        {
            writer.WriteNumber(GenerationIdProperty, generationId);
        }

        writer.WriteNumber(RequestStartedAtProperty, checkpoint.RequestStartedAt.ToUnixTimeMilliseconds());
        writer.WritePropertyName(StepsProperty);
        writer.WriteRawValue(checkpoint.Steps.Span, skipInputValidation: true);
        writer.WriteNumber(TimestampProperty, now.ToUnixTimeMilliseconds());
        writer.WriteEndObject();
    }

    private async Task<(InngestException? Error, bool Retryable)> TrySendAsync(
        Uri url,
        ReadOnlyMemory<byte> body,
        string runId,
        CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _transport.SendAsync(new ApiCall(HttpMethod.Post, url, body), HttpCompletionOption.ResponseContentRead, cancellationToken)
                .ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                return (null, false);
            }

            var status = (int)response.StatusCode;
            var content = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            var reason = ApiHttp.DescribeError(status, content);
            if (response.StatusCode == HttpStatusCode.Conflict)
            {
                return (new InngestException($"Inngest refused the checkpoint of run {runId} because the run was dispatched again: {reason}", status), false);
            }

            return (new InngestException($"Inngest answered {status} to the checkpoint of run {runId}: {reason}", status), IsRetryable(response.StatusCode));
        }
        catch (HttpRequestException exception)
        {
            return (new InngestException($"Could not reach Inngest to checkpoint run {runId}: {exception.Message}", exception), true);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            // HttpClient reports its own timeout this way.
            return (new InngestException($"Checkpointing run {runId} timed out.", exception), true);
        }
    }

    // Answers that a later attempt can turn into a success. Other client errors, such as a run Inngest does not know,
    // fail at once, and the engine falls back to answering with the steps.
    private static bool IsRetryable(HttpStatusCode status) =>
        (int)status >= 500 || status is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests || (int)status == 425;
}
