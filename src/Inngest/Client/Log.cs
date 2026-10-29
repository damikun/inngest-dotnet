// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using Microsoft.Extensions.Logging;

namespace Inngest.Client;

// Event ids 300-399.
internal static partial class Log
{
    [LoggerMessage(EventId = 300, Level = LogLevel.Warning, Message = "SendAsync was called with no events, so nothing was sent to Inngest.")]
    public static partial void NoEventsToSend(ILogger logger);

    [LoggerMessage(EventId = 301, Level = LogLevel.Warning, Message = "Sending events to Inngest failed on attempt {Attempt} of {MaxAttempts}: {Reason} Retrying in {DelayMilliseconds} ms.")]
    public static partial void RetryingSend(ILogger logger, int attempt, int maxAttempts, string reason, int delayMilliseconds);

    [LoggerMessage(EventId = 302, Level = LogLevel.Debug, Message = "Sent {Count} event(s) to Inngest.")]
    public static partial void SentEvents(ILogger logger, int count);

    [LoggerMessage(EventId = 312, Level = LogLevel.Debug, Message = "Inngest answered {StatusCode} for the signing key, so the request is repeated with the fallback key.")]
    public static partial void UsingFallbackSigningKey(ILogger logger, int statusCode);

    [LoggerMessage(EventId = 330, Level = LogLevel.Debug, Message = "Sent signal {Signal}, which resumed run {RunId}.")]
    public static partial void SentSignal(ILogger logger, string signal, string runId);

    [LoggerMessage(EventId = 331, Level = LogLevel.Debug, Message = "Sent signal {Signal}, but no run waits for it.")]
    public static partial void NoRunWaitsForSignal(ILogger logger, string signal);

    [LoggerMessage(EventId = 340, Level = LogLevel.Warning, Message = "Checkpointing run {RunId} failed on attempt {Attempt} of {MaxAttempts}: {Reason} Retrying in {DelayMilliseconds} ms.")]
    public static partial void RetryingCheckpoint(ILogger logger, string runId, int attempt, int maxAttempts, string reason, int delayMilliseconds);
}
