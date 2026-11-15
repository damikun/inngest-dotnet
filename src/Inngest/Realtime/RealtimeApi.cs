// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Inngest.Client;
using Inngest.Configuration;
using Inngest.Internal;
using Microsoft.Extensions.Logging;

namespace Inngest.Realtime;

// POST {api}/v1/realtime/publish?channel=&topic=&run_id= with the message as the body, or with Content-Type text/stream
// and the body streamed, which Inngest relays as a stream; POST {api}/v1/realtime/token with the topics to subscribe
// to, which answers {"jwt": "..."}; and GET {api}/v1/realtime/sse with that token as the bearer, the subscription's
// server-sent events.
internal interface IRealtimeApi
{
    Task PublishAsync(RealtimeTopic topic, string? runId, ReadOnlyMemory<byte> body, CancellationToken cancellationToken);

    // Returns all the text that was streamed.
    Task<string> PublishStreamAsync(RealtimeTopic topic, string? runId, IAsyncEnumerable<string> chunks, CancellationToken cancellationToken);

    Task<string> GetTokenAsync(string channel, IReadOnlyList<string> topics, CancellationToken cancellationToken);

    // The response with the subscription's events, once its headers have arrived. The caller disposes it. A token
    // minted elsewhere may name the API it belongs to; null means the app's.
    Task<HttpResponseMessage> OpenSubscriptionAsync(string token, Uri? apiBaseUrl, CancellationToken cancellationToken);
}

internal sealed class RealtimeApi(IHttpClientFactory httpClientFactory, InngestSettings settings, TimeProvider time, ILogger<RealtimeApi> logger) : IRealtimeApi
{
    private readonly InngestApiTransport _transport = new(httpClientFactory, settings, logger);

    public async Task PublishAsync(RealtimeTopic topic, string? runId, ReadOnlyMemory<byte> body, CancellationToken cancellationToken) =>
        await SendAsync(new ApiCall(HttpMethod.Post, PublishUrl(topic, runId), body), PublishAction(topic), cancellationToken).ConfigureAwait(false);

    public async Task<string> PublishStreamAsync(RealtimeTopic topic, string? runId, IAsyncEnumerable<string> chunks, CancellationToken cancellationToken)
    {
        var content = new TextStreamContent(chunks, time, () => Log.StreamOutgrown(logger, topic.Name, topic.Channel, TextStreamContent.MaxPieces));
        try
        {
            await SendAsync(new ApiCall(HttpMethod.Post, PublishUrl(topic, runId), Content: content, Streamed: true), PublishAction(topic), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception) when (content.Failure is { } failure)
        {
            // The producer failed, not the request: a step whose model gave up for good must not be retried, and the
            // caller may catch the model's own exception.
            failure.Throw();
        }

        return content.Text;
    }

    public async Task<HttpResponseMessage> OpenSubscriptionAsync(string token, Uri? apiBaseUrl, CancellationToken cancellationToken)
    {
        const string Action = "subscribe to realtime messages";
        HttpResponseMessage response;
        try
        {
            response = await _transport.SendAsync(
                new ApiCall(HttpMethod.Get, apiBaseUrl is null ? _transport.Url("/v1/realtime/sse") : new Uri(apiBaseUrl, "/v1/realtime/sse"), Token: token),
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException)
        {
            throw new InngestException($"Failed to {Action}: {exception.Message}", exception);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new InngestException($"Failed to {Action}: the request to Inngest timed out.", exception);
        }

        if (response.IsSuccessStatusCode)
        {
            return response;
        }

        using (response)
        {
            var content = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            throw new InngestException($"Failed to {Action}: {ApiHttp.DescribeError((int)response.StatusCode, content)}", (int)response.StatusCode);
        }
    }

    private Uri PublishUrl(RealtimeTopic topic, string? runId)
    {
        var path = $"/v1/realtime/publish?channel={Uri.EscapeDataString(topic.Channel)}&topic={Uri.EscapeDataString(topic.Name)}";
        if (runId is not null)
        {
            path += $"&run_id={Uri.EscapeDataString(runId)}";
        }

        return _transport.Url(path);
    }

    private static string PublishAction(RealtimeTopic topic) => $"publish to topic \"{topic.Name}\" of channel \"{topic.Channel}\"";

    // [{"channel": <channel>, "name": <topic>, "kind": "run"}, ...], as inngest-js asks for a token.
    public async Task<string> GetTokenAsync(string channel, IReadOnlyList<string> topics, CancellationToken cancellationToken)
    {
        using var body = new PooledBufferWriter();
        using (var writer = new Utf8JsonWriter(body))
        {
            writer.WriteStartArray();
            foreach (var topic in topics)
            {
                writer.WriteStartObject();
                writer.WriteString("channel"u8, channel);
                writer.WriteString("name"u8, topic);
                writer.WriteString("kind"u8, "run");
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        }

        var action = $"get a subscription token for channel \"{channel}\"";
        var content = await SendAsync(new ApiCall(HttpMethod.Post, _transport.Url("/v1/realtime/token"), body.WrittenMemory), action, cancellationToken)
            .ConfigureAwait(false);
        return ReadJwt(content) ?? throw new InngestException(
            $"Failed to {action}: Inngest answered without a token: {ApiHttp.ToText(content) ?? "empty reply"}");
    }

    private async Task<byte[]> SendAsync(ApiCall call, string action, CancellationToken cancellationToken)
    {
        int status;
        byte[] content;
        try
        {
            using var response = await _transport.SendAsync(call, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
            status = (int)response.StatusCode;
            content = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException)
        {
            throw new InngestException($"Failed to {action}: {exception.Message}", exception);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new InngestException($"Failed to {action}: the request to Inngest timed out.", exception);
        }

        if (status is < 200 or > 299)
        {
            throw new InngestException($"Failed to {action}: {ApiHttp.DescribeError(status, content)}", status);
        }

        return content;
    }

    private static string? ReadJwt(byte[] content)
    {
        try
        {
            using var document = JsonDocument.Parse(content);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("jwt", out var jwt)
                && jwt.ValueKind == JsonValueKind.String
                && jwt.GetString() is { Length: > 0 } token
                    ? token
                    : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

// The body of a published message: its data as JSON. Inngest keeps a body that parses as JSON as that JSON, and takes
// any other as a string. inngest-js sends text as it is, so its text that reads as JSON, such as "42" or a model's JSON
// answer, arrives as a number or an object; here text always goes as a JSON string and stays a string.
internal static class RealtimePayload
{
    // Inngest reads at most this much of a message and publishes one cut off there as a string of broken JSON.
    public const int MaxMessageBytes = 512 * 1024;

    public static byte[] Body<T>(T data, JsonSerializerOptions options)
    {
        if (data is not string text)
        {
            return JsonSerializer.SerializeToUtf8Bytes(data, options.GetTypeInfo(typeof(T)));
        }

        // Unless the app chose an encoder, letters outside ASCII go out as UTF-8. Escaped, text in most languages would be
        // two or three times as large, and the 512 KB that Inngest reads would hold that much less of it.
        var body = new ArrayBufferWriter<byte>(text.Length + 2);
        using (var writer = new Utf8JsonWriter(body, new JsonWriterOptions { Encoder = options.Encoder ?? JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            writer.WriteStringValue(text);
        }

        return body.WrittenSpan.ToArray();
    }

    // Why a body is too large to publish, or null.
    public static string? TooLarge(RealtimeTopic topic, int length) =>
        length > MaxMessageBytes
            ? string.Create(
                CultureInfo.InvariantCulture,
                $"Inngest reads at most 512 KB of a realtime message and cuts off the rest, but this message to topic \"{topic.Name}\" of channel \"{topic.Channel}\" is {length:N0} bytes.")
            : null;

    public static void Check(RealtimeTopic topic)
    {
        ArgumentNullException.ThrowIfNull(topic);
        ArgumentException.ThrowIfNullOrEmpty(topic.Channel, nameof(topic));
        ArgumentException.ThrowIfNullOrEmpty(topic.Name, nameof(topic));
    }

    // The topics of a subscription, each once and in a fixed order, so that the same topics in any order are one
    // subscription.
    public static string[] DistinctTopics(IEnumerable<string> topics) =>
        [.. topics.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];

    // What identifies the subscription to these topics (from DistinctTopics) of a channel, for sharing one between
    // everyone who asks for it. The length of each name comes first, so that names which contain a separator cannot run
    // together into the key of another subscription, and let a request in on messages it was not given.
    public static string SubscriptionKey(string channel, string[] topics)
    {
        var key = new StringBuilder();
        key.Append(channel.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(channel);
        foreach (var topic in topics)
        {
            key.Append(';').Append(topic.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(topic);
        }

        return key.ToString();
    }

    public static void Check(string channel, IReadOnlyList<string> topics)
    {
        ArgumentException.ThrowIfNullOrEmpty(channel);
        ArgumentNullException.ThrowIfNull(topics);
        foreach (var topic in topics)
        {
            ArgumentException.ThrowIfNullOrEmpty(topic, nameof(topics));
        }
    }
}
