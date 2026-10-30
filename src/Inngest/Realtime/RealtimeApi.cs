// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using System.Text;
using System.Text.Json;
using Inngest.Client;
using Inngest.Configuration;
using Inngest.Internal;
using Microsoft.Extensions.Logging;

namespace Inngest.Realtime;

// POST {api}/v1/realtime/publish?channel=&topic=&run_id= with the message as the body, and POST {api}/v1/realtime/token
// with the topics to subscribe to, which answers {"jwt": "..."}.
internal interface IRealtimeApi
{
    Task PublishAsync(RealtimeTopic topic, string? runId, ReadOnlyMemory<byte> body, CancellationToken cancellationToken);

    Task<string> GetTokenAsync(string channel, IReadOnlyList<string> topics, CancellationToken cancellationToken);
}

internal sealed class RealtimeApi(IHttpClientFactory httpClientFactory, InngestSettings settings, ILogger<RealtimeApi> logger) : IRealtimeApi
{
    private readonly InngestApiTransport _transport = new(httpClientFactory, settings, logger);

    public async Task PublishAsync(RealtimeTopic topic, string? runId, ReadOnlyMemory<byte> body, CancellationToken cancellationToken)
    {
        var path = $"/v1/realtime/publish?channel={Uri.EscapeDataString(topic.Channel)}&topic={Uri.EscapeDataString(topic.Name)}";
        if (runId is not null)
        {
            path += $"&run_id={Uri.EscapeDataString(runId)}";
        }

        await SendAsync(_transport.Url(path), body, $"publish to topic \"{topic.Name}\" of channel \"{topic.Channel}\"", cancellationToken)
            .ConfigureAwait(false);
    }

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
        var content = await SendAsync(_transport.Url("/v1/realtime/token"), body.WrittenMemory, action, cancellationToken).ConfigureAwait(false);
        return ReadJwt(content) ?? throw new InngestException(
            $"Failed to {action}: Inngest answered without a token: {ApiHttp.ToText(content) ?? "empty reply"}");
    }

    private async Task<byte[]> SendAsync(Uri url, ReadOnlyMemory<byte> body, string action, CancellationToken cancellationToken)
    {
        int status;
        byte[] content;
        try
        {
            using var response = await _transport.SendAsync(new ApiCall(HttpMethod.Post, url, body), HttpCompletionOption.ResponseContentRead, cancellationToken)
                .ConfigureAwait(false);
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

// A message as inngest-js sends it: text as it is, anything else as JSON.
internal static class RealtimeMessage
{
    public static byte[] Body<T>(T data, JsonSerializerOptions options) => data is string text
        ? Encoding.UTF8.GetBytes(text)
        : JsonSerializer.SerializeToUtf8Bytes(data, options.GetTypeInfo(typeof(T)));

    public static void Check(RealtimeTopic topic)
    {
        ArgumentNullException.ThrowIfNull(topic);
        ArgumentException.ThrowIfNullOrEmpty(topic.Channel, nameof(topic));
        ArgumentException.ThrowIfNullOrEmpty(topic.Name, nameof(topic));
    }
}
