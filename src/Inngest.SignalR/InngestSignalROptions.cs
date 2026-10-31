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
    /// The client method that hears of a subscription the client lost, with three arguments: the channel and the topics
    /// as the client asked for them when it joined, so that it joins again with the same call, and the reason. The
    /// reason is "behind" when the client fell more than <see cref="MaxQueuedMessagesPerConnection"/> messages behind, and
    /// what was queued for it was dropped; "send-failed" when a message could not be sent to it; and "inngest-ended"
    /// when Inngest refused or ended the subscription. The connection stays open. "RealtimeError" by default.
    /// </summary>
    public string ErrorMethod { get; set; } = "RealtimeError";

    /// <summary>
    /// How many subscriptions one connection may be in at once, counting each way it asked to join, for each of them can
    /// be a connection from the app to Inngest that nobody else shares. 32 by default.
    /// </summary>
    public int MaxSubscriptionsPerConnection { get; set; } = 32;

    /// <summary>
    /// How many messages may wait to be sent to one connection. A connection that reaches it is too far behind: it loses
    /// its subscriptions and is told through <see cref="ErrorMethod"/>, so that a client that reads slowly holds up
    /// neither the others nor the subscription to Inngest. At least <see cref="MaxSubscriptionsPerConnection"/>, so that
    /// it can be told of each. 512 by default.
    /// </summary>
    public int MaxQueuedMessagesPerConnection { get; set; } = 512;
}
