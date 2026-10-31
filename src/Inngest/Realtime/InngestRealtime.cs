// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Inngest.Configuration;
using Inngest.Execution;
using Microsoft.Extensions.Logging;

namespace Inngest.Realtime;

internal sealed class InngestRealtime(IRealtimeApi api, RealtimeSubscriber subscriber, InngestSettings settings, ILogger<InngestRealtime> logger) : IInngestRealtime
{
    public Task PublishAsync<T>(RealtimeTopic topic, T data, CancellationToken cancellationToken = default)
    {
        RealtimePayload.Check(topic);
        return api.PublishAsync(topic, Invocation.AmbientContext?.RunId, RealtimePayload.Body(data, settings.SerializerOptions), cancellationToken);
    }

    public Task PublishAsync<T>(RealtimeTopic<T> topic, T data, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(topic);
        return PublishAsync(topic.Untyped, data, cancellationToken);
    }

    public Task<string> PublishStreamAsync(RealtimeTopic topic, IAsyncEnumerable<string> chunks, CancellationToken cancellationToken = default)
    {
        RealtimePayload.Check(topic);
        ArgumentNullException.ThrowIfNull(chunks);
        return api.PublishStreamAsync(topic, Invocation.AmbientContext?.RunId, chunks, cancellationToken);
    }

    public async Task<RealtimeToken> GetSubscriptionTokenAsync(string channel, IReadOnlyList<string> topics, CancellationToken cancellationToken = default)
    {
        RealtimePayload.Check(channel, topics);
        var key = await api.GetTokenAsync(channel, topics, cancellationToken).ConfigureAwait(false);
        return new RealtimeToken(channel, [.. topics], key) { ApiBaseUrl = settings.ApiBaseUrl.AbsoluteUri };
    }

    public IAsyncEnumerable<RealtimeMessage<JsonElement>> SubscribeAsync(string channel, IReadOnlyList<string> topics, CancellationToken cancellationToken = default) =>
        SubscribeAsync(channel, topics, null, cancellationToken);

    public IAsyncEnumerable<RealtimeMessage<T>> SubscribeAsync<T>(RealtimeTopic<T> topic, CancellationToken cancellationToken = default) =>
        SubscribeAsync(topic, null, cancellationToken);

    // The same, telling onConnection what the connection to Inngest is doing, which the public members have no way to say.
    internal IAsyncEnumerable<RealtimeMessage<JsonElement>> SubscribeAsync(
        string channel,
        IReadOnlyList<string> topics,
        Action<RealtimeConnection>? onConnection,
        CancellationToken cancellationToken)
    {
        RealtimePayload.Check(channel, topics);
        return Subscribe(channel, [.. topics], new RealtimeMessageReader<JsonElement>(static data => data.Clone(), readDataOfEveryKind: true), onConnection, cancellationToken);
    }

    internal IAsyncEnumerable<RealtimeMessage<T>> SubscribeAsync<T>(
        RealtimeTopic<T> topic,
        Action<RealtimeConnection>? onConnection,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(topic);
        RealtimePayload.Check(topic.Untyped);
        var typeInfo = (JsonTypeInfo<T>)settings.SerializerOptions.GetTypeInfo(typeof(T));
        return Subscribe(topic.Channel, [topic.Name], new RealtimeMessageReader<T>(data => data.Deserialize(typeInfo), readDataOfEveryKind: false), onConnection, cancellationToken);
    }

    private async IAsyncEnumerable<RealtimeMessage<T>> Subscribe<T>(
        string channel,
        IReadOnlyList<string> topics,
        RealtimeMessageReader<T> reader,
        Action<RealtimeConnection>? onConnection,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var payload in subscriber.ReadAsync(channel, topics, onConnection, cancellationToken).ConfigureAwait(false))
        {
            // A message whose data is not a T, as another publisher may send, is skipped rather than ending the subscription.
            RealtimeMessage<T>? message;
            try
            {
                message = reader.Read(payload);
            }
            catch (JsonException exception)
            {
                Log.RealtimeMessageSkipped(logger, channel, typeof(T).Name, exception.Message);
                continue;
            }

            if (message is not null)
            {
                yield return message;
            }
        }
    }
}
