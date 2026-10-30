// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.

using Microsoft.Extensions.Logging;

namespace Inngest.Blazor;

internal static partial class Log
{
    [LoggerMessage(EventId = 600, Level = LogLevel.Warning, Message = "The realtime subscription to channel {Channel} failed.")]
    public static partial void RealtimeViewFailed(ILogger logger, Exception exception, string channel);

    [LoggerMessage(EventId = 601, Level = LogLevel.Warning, Message = "The realtime subscription to channel {Channel} was still running {Seconds} seconds after its component was disposed.")]
    public static partial void RealtimeViewDidNotStop(ILogger logger, string channel, double seconds);

    [LoggerMessage(EventId = 602, Level = LogLevel.Debug, Message = "Subscribed to realtime channel {Channel} through the relay.")]
    public static partial void RelayConnected(ILogger logger, string channel);

    [LoggerMessage(EventId = 603, Level = LogLevel.Warning, Message = "The realtime relay connection for channel {Channel} was lost: {Reason} Connecting again in {DelayMilliseconds} ms.")]
    public static partial void RelayReconnecting(ILogger logger, string channel, string reason, long delayMilliseconds);

    [LoggerMessage(EventId = 604, Level = LogLevel.Warning, Message = "Skipped a realtime message of channel {Channel} that could not be read as {Type}: {Reason}")]
    public static partial void RelayMessageSkipped(ILogger logger, string channel, string type, string reason);
}
