// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using System.Threading.Channels;

namespace Inngest.SignalR;

// A connection that joined feeds. It has a bounded queue of its own and a loop that sends the queue to the connection,
// so that a connection that reads slowly holds up neither the other connections nor the pumps that feed it.
internal sealed class Listener(string connectionId, Action abort, int capacity)
{
    public string ConnectionId { get; } = connectionId;

    // Ends the connection itself, for one that cannot keep up.
    public Action Abort { get; } = abort;

    public Channel<Outgoing> Queue { get; } = Channel.CreateBounded<Outgoing>(new BoundedChannelOptions(capacity) { SingleReader = true, SingleWriter = false });

    // Not disposed, as Feed.Stop: it has no timer, and the connection's end may cancel it while the loop ends.
    public CancellationTokenSource Stop { get; } = new();

    // The feeds the connection is in, under the key they were granted and under the key that was requested when that
    // differs, so that leaving takes either. Guarded by the bridge's lock.
    public Dictionary<string, Feed> Feeds { get; } = new(StringComparer.Ordinal);

    // The feeds in Feeds, each counted once. Guarded by the bridge's lock.
    public int FeedCount { get; set; }

    // Set under the bridge's lock; the pumps read it without.
    public volatile bool Ended;
}

// What a listener's loop sends: a message of Inngest as it is, or, when Channel is set, the news that the feed of that
// channel ended.
internal readonly record struct Outgoing(string? Channel, string Text);
