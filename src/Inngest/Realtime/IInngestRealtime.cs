// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using System.Text.Json;

namespace Inngest;

/// <summary>
/// Realtime through the Inngest API, authenticated with the app's signing key. Inngest delivers a message to the
/// subscribers connected when it is published. Failures throw InngestException with Inngest's message.
/// </summary>
public interface IInngestRealtime
{
    /// <summary>
    /// Publishes data to the topic at once, from anywhere; inside a run it is tagged with the run's id. A retry of
    /// the code publishes again: inside a function, IStepTools.PublishAsync publishes once.
    /// </summary>
    Task PublishAsync<T>(RealtimeTopic topic, T data, CancellationToken cancellationToken = default);

    /// <summary>Publishes data to a typed topic, as PublishAsync does for any topic.</summary>
    Task PublishAsync<T>(RealtimeTopic<T> topic, T data, CancellationToken cancellationToken = default);

    /// <summary>
    /// Streams text to the topic as the chunks arrive, in one request, and returns all of it. Subscribers receive a
    /// StreamStart, the text in StreamChunk messages and a StreamEnd. Inngest takes up to 1,000 chunks of 1 KB a stream.
    /// </summary>
    Task<string> PublishStreamAsync(RealtimeTopic topic, IAsyncEnumerable<string> chunks, CancellationToken cancellationToken = default);

    /// <summary>Mints a token that lets a browser subscribe to these topics of the channel.</summary>
    Task<RealtimeToken> GetSubscriptionTokenAsync(string channel, IReadOnlyList<string> topics, CancellationToken cancellationToken = default);

    /// <summary>
    /// Receives the messages published to these topics of the channel from now on, with their data as JSON, until the
    /// token is cancelled. A lost connection is opened again with a new token; what is published meanwhile is missed.
    /// </summary>
    IAsyncEnumerable<RealtimeMessage<JsonElement>> SubscribeAsync(string channel, IReadOnlyList<string> topics, CancellationToken cancellationToken = default);

    /// <summary>Receives the messages of a typed topic, with their data read as a T, as SubscribeAsync does for a channel.</summary>
    IAsyncEnumerable<RealtimeMessage<T>> SubscribeAsync<T>(RealtimeTopic<T> topic, CancellationToken cancellationToken = default);
}
