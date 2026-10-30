// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.

namespace Inngest;

/// <summary>
/// Where a realtime view stands. A source reports Connecting, Open and Reconnecting; the view adds the rest.
/// </summary>
public enum RealtimeConnectionState
{
    /// <summary>
    /// Nothing is subscribed: the view has not rendered interactively yet, as it never does while prerendering, or the
    /// parameters name nothing to watch.
    /// </summary>
    Idle,

    /// <summary>The subscription is being opened, and no message has arrived yet.</summary>
    Connecting,

    /// <summary>Connected, or messages are arriving. A source that cannot tell connected from connecting leaves it to the first message.</summary>
    Open,

    /// <summary>The connection was lost and a new one is being opened. What is published meanwhile is missed.</summary>
    Reconnecting,

    /// <summary>The source ended the subscription.</summary>
    Closed,

    /// <summary>The subscription failed for good, such as a refused one; Error says why. Restart tries again.</summary>
    Failed,
}
