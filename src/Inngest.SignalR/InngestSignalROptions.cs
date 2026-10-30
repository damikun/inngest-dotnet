// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.

namespace Inngest;

/// <summary>Settings of the bridge that forwards Inngest realtime messages to the clients of a SignalR hub.</summary>
public sealed class InngestSignalROptions
{
    /// <summary>
    /// Decides what a client that asks to join may receive. It returns the subscription to join, which is
    /// <see cref="RealtimeSubscribeRequest.Requested"/> to allow the request as it is, the same channel with fewer
    /// topics to narrow it, or any other subscription, such as a channel derived from the user. It returns null to
    /// refuse, which the client sees as a <see cref="Microsoft.AspNetCore.SignalR.HubException"/>; one that the
    /// callback throws reaches the client with its message. Required: nobody is let in by default. It runs when the
    /// client joins and is not asked again for as long as the client stays.
    /// </summary>
    public Func<RealtimeSubscribeRequest, ValueTask<RealtimeSubscription?>>? Authorize { get; set; }

    /// <summary>
    /// The client method each message is sent to, with the message as Inngest sends it, as JSON in a string, so that the
    /// JSON and MessagePack protocols carry it alike. "Realtime" by default.
    /// </summary>
    public string MessageMethod { get; set; } = "Realtime";

    /// <summary>
    /// The client method that hears of a subscription that Inngest ended, with the channel and a short text. Everyone
    /// who had joined it is out of it, and joins again to try anew. "RealtimeError" by default.
    /// </summary>
    public string ErrorMethod { get; set; } = "RealtimeError";

    /// <summary>
    /// How many channels and topic sets one connection may be joined to at once, for each of them is a connection from
    /// the app to Inngest while nobody else shares it. 32 by default.
    /// </summary>
    public int MaxSubscriptionsPerConnection { get; set; } = 32;
}
