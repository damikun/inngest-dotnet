// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using Microsoft.Extensions.Logging;

namespace Inngest.AspNetCore;

internal static partial class Log
{
    [LoggerMessage(EventId = 360, Level = LogLevel.Warning, Message = "Relaying realtime channel {Channel} stopped: {Reason}")]
    public static partial void RealtimeRelayFailed(ILogger logger, string channel, string reason);

    [LoggerMessage(EventId = 600, Level = LogLevel.Warning,
        Message = "Refused a request to durable endpoint {FunctionId} with 413: its body is larger than the {Limit} bytes its run may store.")]
    public static partial void EndpointBodyTooLarge(ILogger logger, string functionId, int limit);
}
