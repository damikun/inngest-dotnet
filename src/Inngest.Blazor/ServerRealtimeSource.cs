// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.

using System.Text.Json;
using Inngest.Realtime;

namespace Inngest.Blazor;

// On the server a component reads Inngest itself, which IInngestRealtime already does: it reconnects with a new token
// and reads typed data with the app's serializer options. The SDK's own implementation can also say what its connection
// is doing; any other implementation of IInngestRealtime, such as a decorator, is read through the public members and
// says nothing, which leaves the state to the first message.
internal sealed class ServerRealtimeSource(IInngestRealtime realtime) : IRealtimeSource
{
    public IAsyncEnumerable<RealtimeMessage<JsonElement>> SubscribeAsync(
        string channel,
        IReadOnlyList<string> topics,
        Action<RealtimeConnectionState>? onConnectionChanged,
        CancellationToken cancellationToken = default) =>
        realtime is InngestRealtime sdk && onConnectionChanged is not null
            ? sdk.SubscribeAsync(channel, topics, Report(onConnectionChanged), cancellationToken)
            : realtime.SubscribeAsync(channel, topics, cancellationToken);

    public IAsyncEnumerable<RealtimeMessage<T>> SubscribeAsync<T>(
        RealtimeTopic<T> topic,
        Action<RealtimeConnectionState>? onConnectionChanged,
        CancellationToken cancellationToken = default) =>
        realtime is InngestRealtime sdk && onConnectionChanged is not null
            ? sdk.SubscribeAsync(topic, Report(onConnectionChanged), cancellationToken)
            : realtime.SubscribeAsync(topic, cancellationToken);

    private static Action<RealtimeConnection> Report(Action<RealtimeConnectionState> onConnectionChanged) => connection =>
        onConnectionChanged(connection switch
        {
            RealtimeConnection.Open => RealtimeConnectionState.Open,
            RealtimeConnection.Reconnecting => RealtimeConnectionState.Reconnecting,
            _ => RealtimeConnectionState.Connecting,
        });
}
