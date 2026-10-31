// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.

using System.Threading.Channels;

namespace Inngest.SignalR;

// A connection that joined feeds. It has a bounded queue of its own and a loop that sends the queue to the connection,
// so that a connection that reads slowly holds up neither the other connections nor the pumps that feed it.
internal sealed class Listener(string connectionId, int capacity)
{
    public string ConnectionId { get; } = connectionId;

    // The loop reads it, and the bridge empties it when the connection has to start over, so it has more than one reader.
    public Channel<Outgoing> Queue { get; } = Channel.CreateBounded<Outgoing>(new BoundedChannelOptions(capacity) { SingleReader = false, SingleWriter = false });

    // Not disposed, as Feed.Stop: it has no timer, and the connection's end may cancel it while the loop ends.
    public CancellationTokenSource Stop { get; } = new();

    // What the connection asked to join, by the key of what it asked for. Guarded by the bridge's lock.
    public Dictionary<string, Request> Requests { get; } = new(StringComparer.Ordinal);

    // Set under the bridge's lock; the pumps read it without.
    public volatile bool Ended;
}

// One subscription as the client has it: the channel and topics it asked for, which Authorize may have turned into
// another feed. The client gets these back when the subscription is lost, to join it again.
internal sealed record Request(Feed Feed, string Channel, string[] Topics);

// What a listener's loop sends: a message of Inngest as it is, or, when Channel is set, the news that the subscription to
// these topics of that channel is lost, with the reason as the text.
internal readonly record struct Outgoing(string Text, string? Channel = null, string[]? Topics = null);
