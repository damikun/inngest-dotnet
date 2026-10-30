// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using Microsoft.Extensions.Logging;

namespace Inngest.SignalR;

internal static partial class Log
{
    [LoggerMessage(EventId = 370, Level = LogLevel.Debug, Message = "Forwarding realtime channel {Channel} to SignalR clients.")]
    public static partial void FeedOpened(ILogger logger, string channel);

    [LoggerMessage(EventId = 371, Level = LogLevel.Debug, Message = "No SignalR client listens to realtime channel {Channel} any more, so its subscription to Inngest is closed.")]
    public static partial void FeedClosed(ILogger logger, string channel);

    [LoggerMessage(EventId = 372, Level = LogLevel.Warning, Message = "Forwarding realtime channel {Channel} to SignalR clients stopped: {Reason}")]
    public static partial void FeedFailed(ILogger logger, string channel, string reason);

    [LoggerMessage(EventId = 373, Level = LogLevel.Warning, Message = "SignalR connection {ConnectionId} is disconnected: it fell more than {Capacity} realtime messages behind.")]
    public static partial void ListenerFellBehind(ILogger logger, string connectionId, int capacity);

    [LoggerMessage(EventId = 374, Level = LogLevel.Warning, Message = "SignalR connection {ConnectionId} is disconnected: sending it a realtime message failed: {Reason}")]
    public static partial void ListenerSendFailed(ILogger logger, string connectionId, string reason);
}
