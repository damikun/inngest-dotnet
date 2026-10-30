// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

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

    /// <summary>Mints a token that lets a browser subscribe to these topics of the channel.</summary>
    Task<RealtimeToken> GetSubscriptionTokenAsync(string channel, IReadOnlyList<string> topics, CancellationToken cancellationToken = default);
}
