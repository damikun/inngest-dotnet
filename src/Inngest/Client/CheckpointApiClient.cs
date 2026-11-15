// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using System.Net;
using System.Text.Json;
using Inngest.Configuration;
using Inngest.Internal;
using Microsoft.Extensions.Logging;

namespace Inngest.Client;

// POST {api}/v1/checkpoint/{runId}/async, retried like inngest-js with up to 5 attempts and backoff from 100 ms. A
// 409 is never retried: Inngest has dispatched the run again, and the steps belong to that newer call. The runs of
// durable endpoint requests are created and extended the same way, through POST {api}/v1/checkpoint and
// {api}/v1/checkpoint/{runId}/steps. All three name the app instance in X-Inngest-Instance-Id when it has an id, as
// inngest-js does.
internal sealed class CheckpointApiClient : ICheckpointApi, IEndpointCheckpointApi
{
    internal const int MaxAttempts = 5;

    // Inngest reads at most this much of the call that creates a run, and fails to parse the rest.
    internal const int MaxNewRunLength = 4 * 1024 * 1024;

    // The execution version of the calls Inngest makes when it resumes the run.
    private const int RequestVersion = 2;

    // How a 409 to an async checkpoint reads: Inngest has dispatched the run again.
    private const string DispatchedAgain = "because the run was dispatched again";

    private static readonly JsonEncodedText RunIdProperty = JsonEncodedText.Encode("run_id");
    private static readonly JsonEncodedText FunctionIdProperty = JsonEncodedText.Encode("fn_id");
    private static readonly JsonEncodedText AppIdProperty = JsonEncodedText.Encode("app_id");
    private static readonly JsonEncodedText QueueItemIdProperty = JsonEncodedText.Encode("qi_id");
    private static readonly JsonEncodedText RequestIdProperty = JsonEncodedText.Encode("request_id");
    private static readonly JsonEncodedText GenerationIdProperty = JsonEncodedText.Encode("generation_id");
    private static readonly JsonEncodedText RequestStartedAtProperty = JsonEncodedText.Encode("request_started_at");
    private static readonly JsonEncodedText StepsProperty = JsonEncodedText.Encode("steps");
    private static readonly JsonEncodedText TimestampProperty = JsonEncodedText.Encode("ts");
    private static readonly JsonEncodedText EventProperty = JsonEncodedText.Encode("event");
    private static readonly JsonEncodedText RequestVersionProperty = JsonEncodedText.Encode("request_version");
    private static readonly JsonEncodedText RetriesProperty = JsonEncodedText.Encode("retries");

    private readonly InngestApiTransport _transport;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<CheckpointApiClient> _logger;
    private readonly CheckpointInstance _instance;

    public CheckpointApiClient(
        IHttpClientFactory httpClientFactory,
        InngestSettings settings,
        TimeProvider timeProvider,
        ILogger<CheckpointApiClient> logger,
        CheckpointInstance? instance = null)
    {
        _transport = new InngestApiTransport(httpClientFactory, settings, logger);
        _timeProvider = timeProvider;
        _logger = logger;
        _instance = instance ?? new CheckpointInstance(settings);
    }

    public async Task CheckpointAsync(AsyncCheckpoint checkpoint, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(checkpoint.RunId);

        var url = _transport.Url($"/v1/checkpoint/{Uri.EscapeDataString(checkpoint.RunId)}/async");
        await SendAsync(url, checkpoint.Steps.Length, (body, now) => WriteBody(body, checkpoint, now), checkpoint.RunId, DispatchedAgain, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<EndpointRun> CheckpointNewRunAsync(NewEndpointRun run, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(run.RunId);

        var reply = await SendAsync(
            _transport.Url("/v1/checkpoint"),
            run.Event.Length + run.Steps.Length,
            (body, now) => WriteBody(body, run, now),
            run.RunId,
            "because the run already exists",
            cancellationToken,
            length => length > MaxNewRunLength ? TooLarge(run, length) : null).ConfigureAwait(false);
        return ReadRun(reply, run.RunId);
    }

    public async Task CheckpointEndpointStepsAsync(EndpointRun run, ReadOnlyMemory<byte> steps, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);

        var url = _transport.Url($"/v1/checkpoint/{Uri.EscapeDataString(run.RunId)}/steps");
        await SendAsync(url, steps.Length, (body, now) => WriteBody(body, run, steps, now), run.RunId, "with a conflict", cancellationToken)
            .ConfigureAwait(false);
    }

    // Returns Inngest's reply to the attempt that succeeded.
    private async Task<byte[]> SendAsync(
        Uri url,
        int payloadLength,
        Action<PooledBufferWriter, DateTimeOffset> writeBody,
        string runId,
        string conflict,
        CancellationToken cancellationToken,
        Func<int, InngestException?>? tooLarge = null)
    {
        using var body = new PooledBufferWriter(payloadLength + 512);
        for (var attempt = 1; ; attempt++)
        {
            // Rewritten for every attempt, so that "ts" says when Inngest was asked.
            body.Clear();
            writeBody(body, _timeProvider.GetUtcNow());
            if (tooLarge?.Invoke(body.WrittenCount) is { } exception)
            {
                throw exception;
            }

            var (reply, error, retryable) = await TrySendAsync(url, body.WrittenMemory, runId, conflict, cancellationToken).ConfigureAwait(false);
            if (error is null)
            {
                return reply!;
            }

            if (attempt == MaxAttempts || !retryable)
            {
                throw error;
            }

            var delay = InngestClient.GetBackoffDelay(attempt, Random.Shared.NextDouble());
            Log.RetryingCheckpoint(_logger, runId, attempt, MaxAttempts, error.Message, (int)delay.TotalMilliseconds);
            await Task.Delay(delay, _timeProvider, cancellationToken).ConfigureAwait(false);
        }
    }

    // Names what makes the call that creates a run larger than Inngest reads: the request's event, or the ops that go
    // with it, which can be a step's output, an error or the endpoint's response. The rest of the call takes about a
    // hundred bytes, so an event just under the limit can be too large without its ops.
    private static InngestException TooLarge(NewEndpointRun run, int length)
    {
        var opsLength = run.Steps.IsEmpty ? 2 : run.Steps.Length;
        if (length - opsLength > MaxNewRunLength)
        {
            return new InngestException(
                $"Run {run.RunId} was not created: without its ops, the call that creates it comes to {length - opsLength} bytes, more than " +
                $"the {MaxNewRunLength} bytes Inngest reads of it, and the request's event takes {run.Event.Length} of them. " +
                "Lower DurableEndpointOptions.MaxRequestBodySize.");
        }

        var ops = DescribeOps(run.Steps);
        var which = ops.Count == 1 ? "the op" : "the ops";
        return new InngestException(
            $"Run {run.RunId} was not created: the call that creates it comes to {length} bytes, more than the {MaxNewRunLength} bytes " +
            $"Inngest reads of it. The request's event takes {run.Event.Length} bytes of it, and {which} {string.Join(", ", ops)} {opsLength}. " +
            $"Lower DurableEndpointOptions.MaxRequestBodySize, or make {which} smaller, such as the output of a step or the message of an error.");
    }

    // Each op by its kind and the name of its step, such as StepRun "price".
    private static List<string> DescribeOps(ReadOnlyMemory<byte> steps)
    {
        var ops = new List<string>();
        try
        {
            using var document = JsonDocument.Parse(steps);
            foreach (var op in document.RootElement.EnumerateArray())
            {
                var kind = Text(op, "op"u8) ?? "of unknown kind";
                ops.Add(Text(op, "displayName"u8) is { } name ? $"{kind} \"{name}\"" : kind switch
                {
                    "RunComplete" => "RunComplete (the endpoint's response)",
                    "StepError" or "StepFailed" => $"{kind} (the endpoint's own error)",
                    _ => kind,
                });
            }
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            // Named by their size alone.
        }

        return ops;
    }

    private static string? Text(JsonElement element, ReadOnlySpan<byte> name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    // The reply's data holds the ids of the run Inngest created and the token for its output.
    internal static EndpointRun ReadRun(ReadOnlySpan<byte> reply, string runId)
    {
        try
        {
            var reader = new Utf8JsonReader(reply);
            using var document = JsonDocument.ParseValue(ref reader);
            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("data"u8, out var data)
                && data.ValueKind == JsonValueKind.Object
                && String(data, "fn_id"u8) is { } functionId
                && String(data, "app_id"u8) is { } appId)
            {
                return new EndpointRun(String(data, "run_id"u8) ?? runId, functionId, appId, String(data, "token"u8));
            }
        }
        catch (JsonException)
        {
            // Reported below with what Inngest sent.
        }

        throw new InngestException(
            $"Inngest created run {runId} but its reply did not say which function and app it belongs to: {ApiHttp.ToText(reply) ?? "(empty)"}");

        static string? String(JsonElement data, ReadOnlySpan<byte> name) =>
            data.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text
                ? text
                : null;
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

    // The order of inngest-js.
    internal static void WriteBody(PooledBufferWriter body, NewEndpointRun run, DateTimeOffset now)
    {
        using var writer = new Utf8JsonWriter(body);
        writer.WriteStartObject();
        writer.WriteString(RunIdProperty, run.RunId);
        writer.WritePropertyName(EventProperty);
        writer.WriteRawValue(run.Event.Span, skipInputValidation: true);
        writer.WritePropertyName(StepsProperty);
        writer.WriteRawValue(run.Steps.IsEmpty ? "[]"u8 : run.Steps.Span, skipInputValidation: true);
        writer.WriteNumber(TimestampProperty, now.ToUnixTimeMilliseconds());
        writer.WriteNumber(RequestVersionProperty, RequestVersion);
        writer.WriteNumber(RetriesProperty, run.Retries);
        writer.WriteEndObject();
    }

    internal static void WriteBody(PooledBufferWriter body, EndpointRun run, ReadOnlyMemory<byte> steps, DateTimeOffset now)
    {
        using var writer = new Utf8JsonWriter(body);
        writer.WriteStartObject();
        writer.WriteString(FunctionIdProperty, run.FunctionId);
        writer.WriteString(AppIdProperty, run.AppId);
        writer.WriteString(RunIdProperty, run.RunId);
        writer.WritePropertyName(StepsProperty);
        writer.WriteRawValue(steps.Span, skipInputValidation: true);
        writer.WriteNumber(TimestampProperty, now.ToUnixTimeMilliseconds());
        writer.WriteEndObject();
    }

    private async Task<(byte[]? Reply, InngestException? Error, bool Retryable)> TrySendAsync(
        Uri url,
        ReadOnlyMemory<byte> body,
        string runId,
        string conflict,
        CancellationToken cancellationToken)
    {
        try
        {
            var call = new ApiCall(HttpMethod.Post, url, body, InstanceId: _instance.Id);
            using var response = await _transport.SendAsync(call, HttpCompletionOption.ResponseContentRead, cancellationToken)
                .ConfigureAwait(false);
            var content = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                return (content, null, false);
            }

            var status = (int)response.StatusCode;
            var reason = ApiHttp.DescribeError(status, content);
            if (response.StatusCode == HttpStatusCode.Conflict)
            {
                return (null, new InngestException($"Inngest refused the checkpoint of run {runId} {conflict}: {reason}", status), false);
            }

            return (null, new InngestException($"Inngest answered {status} to the checkpoint of run {runId}: {reason}", status), IsRetryable(response.StatusCode));
        }
        catch (HttpRequestException exception)
        {
            return (null, new InngestException($"Could not reach Inngest to checkpoint run {runId}: {exception.Message}", exception), true);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            // HttpClient reports its own timeout this way.
            return (null, new InngestException($"Checkpointing run {runId} timed out.", exception), true);
        }
    }

    // Answers that a later attempt can turn into a success. Other client errors, such as a run Inngest does not know,
    // fail at once, and the engine falls back to answering with the steps.
    private static bool IsRetryable(HttpStatusCode status) =>
        (int)status >= 500 || status is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests || (int)status == 425;
}
