// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Inngest.Configuration;
using Inngest.Execution;

namespace Inngest.Realtime;

internal sealed class InngestRealtime(IRealtimeApi api, RealtimeSubscriber subscriber, InngestSettings settings) : IInngestRealtime
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

    public IAsyncEnumerable<RealtimeMessage<JsonElement>> SubscribeAsync(string channel, IReadOnlyList<string> topics, CancellationToken cancellationToken = default)
    {
        RealtimePayload.Check(channel, topics);
        return Subscribe(channel, [.. topics], new RealtimeMessageReader<JsonElement>(static data => data.Clone(), readDataOfEveryKind: true), cancellationToken);
    }

    public IAsyncEnumerable<RealtimeMessage<T>> SubscribeAsync<T>(RealtimeTopic<T> topic, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(topic);
        RealtimePayload.Check(topic.Untyped);
        var typeInfo = (JsonTypeInfo<T>)settings.SerializerOptions.GetTypeInfo(typeof(T));
        return Subscribe(topic.Channel, [topic.Name], new RealtimeMessageReader<T>(data => data.Deserialize(typeInfo), readDataOfEveryKind: false), cancellationToken);
    }

    private async IAsyncEnumerable<RealtimeMessage<T>> Subscribe<T>(
        string channel,
        IReadOnlyList<string> topics,
        RealtimeMessageReader<T> reader,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var payload in subscriber.ReadAsync(channel, topics, cancellationToken).ConfigureAwait(false))
        {
            if (reader.Read(payload) is { } message)
            {
                yield return message;
            }
        }
    }
}
