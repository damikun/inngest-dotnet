// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.

using System.Text.Json;

namespace Inngest;

/// <summary>
/// Where UI code gets realtime messages from, so that a component does not care where it runs. On the server the
/// source reads Inngest with the app's signing key (AddInngestRealtimeServerSource); in WebAssembly, which holds no key,
/// it reads the server-sent events of the app's MapInngestRealtime endpoint (AddInngestRealtimeRelaySource). An app
/// that renders in both, such as interactive Auto, registers one in each project. Like IInngestRealtime, a source
/// receives the messages published from now on, and a connection it loses is opened again; what is published meanwhile
/// is missed.
/// </summary>
public interface IRealtimeSource
{
    /// <summary>
    /// Receives the messages of these topics of the channel from now on, with their data as JSON, until the token is
    /// cancelled. A subscription that fails for good, such as a refused one, throws from the enumeration.
    /// </summary>
    IAsyncEnumerable<RealtimeMessage<JsonElement>> SubscribeAsync(string channel, IReadOnlyList<string> topics, CancellationToken cancellationToken = default) =>
        SubscribeAsync(channel, topics, null, cancellationToken);

    /// <summary>
    /// Receives the messages of a typed topic, with their data read as a T by the serializer options of the app. A
    /// message whose data is not a T is skipped, with a warning in the log.
    /// </summary>
    IAsyncEnumerable<RealtimeMessage<T>> SubscribeAsync<T>(RealtimeTopic<T> topic, CancellationToken cancellationToken = default) =>
        SubscribeAsync(topic, null, cancellationToken);

    /// <summary>
    /// Receives the messages of these topics of the channel, as the overload without a callback does, and tells
    /// onConnectionChanged when the connection opens, is lost and opens again.
    /// </summary>
    /// <param name="channel">The channel to watch.</param>
    /// <param name="topics">The topics of the channel to receive.</param>
    /// <param name="onConnectionChanged">
    /// Told Connecting, Open and Reconnecting by a source that can tell, from the thread that reads, so it should return
    /// at once.
    /// </param>
    /// <param name="cancellationToken">Ends the subscription.</param>
    IAsyncEnumerable<RealtimeMessage<JsonElement>> SubscribeAsync(
        string channel,
        IReadOnlyList<string> topics,
        Action<RealtimeConnectionState>? onConnectionChanged,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Receives the messages of a typed topic, as the overload without a callback does, and tells onConnectionChanged
    /// when the connection opens, is lost and opens again.
    /// </summary>
    /// <param name="topic">The topic to watch.</param>
    /// <param name="onConnectionChanged">As for the untyped overload.</param>
    /// <param name="cancellationToken">Ends the subscription.</param>
    IAsyncEnumerable<RealtimeMessage<T>> SubscribeAsync<T>(
        RealtimeTopic<T> topic,
        Action<RealtimeConnectionState>? onConnectionChanged,
        CancellationToken cancellationToken = default);
}
