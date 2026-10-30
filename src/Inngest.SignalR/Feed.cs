// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.

namespace Inngest.SignalR;

// One subscription to Inngest, for these topics of a channel, shared by every connection that joined them.
internal sealed class Feed(string key, string channel, string[] topics)
{
    public string Key { get; } = key;

    public string Channel { get; } = channel;

    public string[] Topics { get; } = topics;

    // Not disposed: it has no timer, and a connection may still cancel it while the pump ends.
    public CancellationTokenSource Stop { get; } = new();

    // Replaced under the bridge's lock, never changed, so the pump reads the current listeners without it.
    public Listener[] Listeners = [];
}
