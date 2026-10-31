// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.

using Microsoft.Extensions.Logging;

namespace Inngest.SignalR;

internal static partial class Log
{
    [LoggerMessage(EventId = 610, Level = LogLevel.Debug, Message = "Forwarding realtime channel {Channel} to SignalR clients.")]
    public static partial void FeedOpened(ILogger logger, string channel);

    [LoggerMessage(EventId = 611, Level = LogLevel.Debug, Message = "No SignalR client listens to realtime channel {Channel} any more, so its subscription to Inngest is closed.")]
    public static partial void FeedClosed(ILogger logger, string channel);

    [LoggerMessage(EventId = 612, Level = LogLevel.Warning, Message = "Forwarding realtime channel {Channel} to SignalR clients stopped: {Reason}")]
    public static partial void FeedFailed(ILogger logger, string channel, string reason);

    [LoggerMessage(EventId = 613, Level = LogLevel.Warning, Message = "SignalR connection {ConnectionId} lost its subscriptions: it fell more than {Capacity} realtime messages behind.")]
    public static partial void ListenerFellBehind(ILogger logger, string connectionId, int capacity);

    [LoggerMessage(EventId = 614, Level = LogLevel.Warning, Message = "SignalR connection {ConnectionId} lost its subscriptions: sending it a realtime message failed: {Reason}")]
    public static partial void ListenerSendFailed(ILogger logger, string connectionId, string reason);
}
