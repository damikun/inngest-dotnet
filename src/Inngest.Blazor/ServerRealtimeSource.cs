// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.

using System.Text.Json;

namespace Inngest.Blazor;

// On the server a component reads Inngest itself, which IInngestRealtime already does: it reconnects with a new token
// and reads typed data with the app's serializer options. It cannot tell when the connection is up, so it reports nothing.
internal sealed class ServerRealtimeSource(IInngestRealtime realtime) : IRealtimeSource
{
    public IAsyncEnumerable<RealtimeMessage<JsonElement>> SubscribeAsync(
        string channel,
        IReadOnlyList<string> topics,
        CancellationToken cancellationToken = default,
        Action<RealtimeConnectionState>? onConnectionChanged = null) =>
        realtime.SubscribeAsync(channel, topics, cancellationToken);

    public IAsyncEnumerable<RealtimeMessage<T>> SubscribeAsync<T>(
        RealtimeTopic<T> topic,
        CancellationToken cancellationToken = default,
        Action<RealtimeConnectionState>? onConnectionChanged = null) =>
        realtime.SubscribeAsync(topic, cancellationToken);
}
