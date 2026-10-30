// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.

using Microsoft.AspNetCore.SignalR;

namespace Inngest;

/// <summary>
/// A SignalR hub that lets clients join and leave the realtime messages of Inngest: <c>Subscribe(channel, topics)</c>
/// and <c>Unsubscribe(channel, topics)</c>. Register it with <c>AddInngestSignalR</c> and map it with
/// <c>MapInngestRealtimeHub</c>. An app that has a hub of its own calls <see cref="InngestRealtimeBridge{THub}"/> from
/// that hub instead.
/// </summary>
/// <param name="bridge">Does the work.</param>
public sealed class InngestRealtimeHub(InngestRealtimeBridge<InngestRealtimeHub> bridge) : Hub
{
    /// <summary>
    /// Joins the caller to these topics of the channel if <see cref="InngestSignalROptions.Authorize"/> lets it in.
    /// The caller receives the messages from now on, until it leaves or disconnects, and joins again after a reconnect.
    /// </summary>
    public async Task Subscribe(string channel, string[] topics) =>
        await bridge.SubscribeAsync(Context, channel, topics, Context.ConnectionAborted).ConfigureAwait(false);

    /// <summary>Takes the caller out of the topics it joined with the same arguments. Leaving what it is not in does nothing.</summary>
    public void Unsubscribe(string channel, string[] topics) => bridge.Unsubscribe(Context, channel, topics);
}
