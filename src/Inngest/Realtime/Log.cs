// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using Microsoft.Extensions.Logging;

namespace Inngest.Realtime;

internal static partial class Log
{
    [LoggerMessage(EventId = 350, Level = LogLevel.Debug, Message = "Subscribed to realtime channel {Channel}.")]
    public static partial void RealtimeConnected(ILogger logger, string channel);

    [LoggerMessage(EventId = 351, Level = LogLevel.Warning, Message = "The realtime subscription to channel {Channel} was lost: {Reason} Subscribing again in {DelayMilliseconds} ms.")]
    public static partial void RealtimeReconnecting(ILogger logger, string channel, string reason, long delayMilliseconds);
}
