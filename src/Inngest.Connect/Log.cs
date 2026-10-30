// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using Inngest.Connect.Protocol;
using Microsoft.Extensions.Logging;

namespace Inngest.Connect;

// Event ids 500-599.
internal static partial class Log
{
    [LoggerMessage(EventId = 500, Level = LogLevel.Information, Message = "Connected to Inngest as connection {ConnectionId} (gateway group {GatewayGroup}).")]
    public static partial void Connected(ILogger logger, string connectionId, string gatewayGroup);

    [LoggerMessage(EventId = 501, Level = LogLevel.Warning, Message = "Connecting to Inngest failed ({Failures} in a row): {Reason} Retrying in {Delay}.")]
    public static partial void ConnectFailed(ILogger logger, int failures, TimeSpan delay, string reason);

    [LoggerMessage(EventId = 502, Level = LogLevel.Error, Message = "Connecting to Inngest failed unexpectedly ({Failures} in a row). Retrying in {Delay}.")]
    public static partial void ConnectFailedUnexpectedly(ILogger logger, int failures, TimeSpan delay, Exception exception);

    [LoggerMessage(EventId = 503, Level = LogLevel.Information, Message = "Inngest rejected the signing key, so the next attempt to connect uses the {Key} key.")]
    public static partial void SwitchedSigningKey(ILogger logger, string key);

    [LoggerMessage(EventId = 504, Level = LogLevel.Error, Message = "Inngest refused to connect the worker because the account has as many connections as its plan allows.")]
    public static partial void ConnectionLimitReached(ILogger logger);

    [LoggerMessage(EventId = 505, Level = LogLevel.Error, Message = "{Reason} Fix the app's configuration; the worker keeps retrying.")]
    public static partial void SyncFailed(ILogger logger, string reason);

    [LoggerMessage(EventId = 506, Level = LogLevel.Warning, Message = "Lost connection {ConnectionId} to Inngest ({Reason}); reconnecting.")]
    public static partial void ConnectionLost(ILogger logger, string connectionId, string reason);

    [LoggerMessage(EventId = 507, Level = LogLevel.Information, Message = "The gateway of connection {ConnectionId} is draining; moving to a new connection.")]
    public static partial void GatewayDraining(ILogger logger, string connectionId);

    [LoggerMessage(EventId = 508, Level = LogLevel.Warning, Message = "The gateway stopped answering heartbeats on connection {ConnectionId}; reconnecting.")]
    public static partial void HeartbeatsMissed(ILogger logger, string connectionId);

    [LoggerMessage(EventId = 509, Level = LogLevel.Information, Message = "Shutting down the Connect worker after {Count} running executions finish.")]
    public static partial void ShuttingDown(ILogger logger, int count);

    [LoggerMessage(EventId = 510, Level = LogLevel.Information, Message = "Disconnected from Inngest.")]
    public static partial void Disconnected(ILogger logger);

    [LoggerMessage(EventId = 511, Level = LogLevel.Debug, Message = "Received request {RequestId} for function {FunctionSlug} in run {RunId}.")]
    public static partial void RequestReceived(ILogger logger, string requestId, string functionSlug, string runId);

    [LoggerMessage(EventId = 512, Level = LogLevel.Warning, Message = "Skipped request {RequestId} for app {AppName}; this worker runs app {AppId}.")]
    public static partial void RequestForOtherApp(ILogger logger, string requestId, string appName, string appId);

    [LoggerMessage(EventId = 513, Level = LogLevel.Information, Message = "Skipped request {RequestId} for function {FunctionSlug}, which arrived during shutdown; Inngest gives it to another worker.")]
    public static partial void RequestDuringShutdown(ILogger logger, string requestId, string functionSlug);

    [LoggerMessage(EventId = 514, Level = LogLevel.Debug, Message = "Request {RequestId} is running already; acknowledged it again and kept the newer lease.")]
    public static partial void RequestAlreadyRunning(ILogger logger, string requestId);

    [LoggerMessage(EventId = 515, Level = LogLevel.Error, Message = "Inngest called function {FunctionSlug}, which this app does not have; is the app synced?")]
    public static partial void FunctionNotFound(ILogger logger, string functionSlug);

    [LoggerMessage(EventId = 516, Level = LogLevel.Error, Message = "Running request {RequestId} of function {FunctionSlug} failed.")]
    public static partial void ExecutionFailed(ILogger logger, string requestId, string functionSlug, Exception exception);

    [LoggerMessage(EventId = 517, Level = LogLevel.Warning, Message = "Cancelled request {RequestId} of function {FunctionSlug} because the worker stopped before it finished.")]
    public static partial void ExecutionCancelled(ILogger logger, string requestId, string functionSlug);

    [LoggerMessage(EventId = 518, Level = LogLevel.Warning,
        Message = "Inngest did not extend the lease of request {RequestId} in run {RunId}; another worker may take the request over, and this execution's result may be discarded.")]
    public static partial void LeaseLost(ILogger logger, string requestId, string runId);

    [LoggerMessage(EventId = 519, Level = LogLevel.Warning, Message = "Could not extend the lease of request {RequestId} because no connection is open.")]
    public static partial void CannotExtendLease(ILogger logger, string requestId);

    [LoggerMessage(EventId = 520, Level = LogLevel.Warning, Message = "No connection is open for the reply to request {RequestId}; sending it over HTTP.")]
    public static partial void ReplyWithoutConnection(ILogger logger, string requestId);

    [LoggerMessage(EventId = 521, Level = LogLevel.Warning, Message = "Inngest did not acknowledge the reply to request {RequestId} within {Deadline}; sending it over HTTP.")]
    public static partial void ReplyNotAcknowledged(ILogger logger, string requestId, TimeSpan deadline);

    [LoggerMessage(EventId = 522, Level = LogLevel.Debug, Message = "Sent the reply to request {RequestId} over HTTP.")]
    public static partial void Flushed(ILogger logger, string requestId);

    [LoggerMessage(EventId = 523, Level = LogLevel.Warning, Message = "Could not send the reply to request {RequestId} over HTTP: {Reason}.")]
    public static partial void FlushFailed(ILogger logger, string requestId, string reason);

    [LoggerMessage(EventId = 524, Level = LogLevel.Error, Message = "Gave up sending {Count} replies over HTTP after {Rounds} rounds; they are tried again on the next connection.")]
    public static partial void FlushGaveUp(ILogger logger, int count, int rounds);

    [LoggerMessage(EventId = 525, Level = LogLevel.Warning, Message = "Dropped the reply to request {RequestId}, which could not be delivered before the worker stopped.")]
    public static partial void ReplyDropped(ILogger logger, string requestId);

    [LoggerMessage(EventId = 526, Level = LogLevel.Debug, Message = "Ignored a {Kind} message on connection {ConnectionId}.")]
    public static partial void UnexpectedMessage(ILogger logger, GatewayMessageType kind, string connectionId);

    [LoggerMessage(EventId = 530, Level = LogLevel.Warning, Message = "Ignored a message of {Length} bytes on connection {ConnectionId} that is not a ConnectMessage.")]
    public static partial void InvalidMessage(ILogger logger, string connectionId, int length);

    [LoggerMessage(EventId = 531, Level = LogLevel.Warning, Message = "Ignored a {Kind} message on connection {ConnectionId} whose payload cannot be read.")]
    public static partial void InvalidPayload(ILogger logger, GatewayMessageType kind, string connectionId, Exception exception);

    [LoggerMessage(EventId = 532, Level = LogLevel.Error, Message = "Reading from connection {ConnectionId} failed unexpectedly.")]
    public static partial void ReadLoopFailed(ILogger logger, string connectionId, Exception exception);

    [LoggerMessage(EventId = 533, Level = LogLevel.Error, Message = "Writing to connection {ConnectionId} failed unexpectedly.")]
    public static partial void WriteLoopFailed(ILogger logger, string connectionId, Exception exception);

    [LoggerMessage(EventId = 534, Level = LogLevel.Error,
        Message = "Inngest refused the reply to request {RequestId} with status {StatusCode}, so it was dropped; Inngest retries the request once its lease runs out.")]
    public static partial void ReplyRefused(ILogger logger, string requestId, int statusCode);

    [LoggerMessage(EventId = 535, Level = LogLevel.Debug,
        Message = "Ignored the answer to an earlier lease extension of request {RequestId}; Inngest has handed out a newer lease since.")]
    public static partial void StaleLeaseAnswer(ILogger logger, string requestId);
}
