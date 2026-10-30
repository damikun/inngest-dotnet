// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using Microsoft.Extensions.Logging;

namespace Inngest.Serving;

internal static partial class Log
{
    [LoggerMessage(EventId = 400, Level = LogLevel.Error,
        Message = "Inngest runs in cloud mode but no signing key is configured. Set INNGEST_SIGNING_KEY, or INNGEST_DEV=1 for local development.")]
    public static partial void MissingSigningKey(ILogger logger);

    [LoggerMessage(EventId = 401, Level = LogLevel.Warning, Message = "Rejected an Inngest {Method} request: {Reason}")]
    public static partial void Unauthorized(ILogger logger, string method, string? reason);

    [LoggerMessage(EventId = 414, Level = LogLevel.Debug, Message = "Rejected an unsigned Inngest {Method} request")]
    public static partial void Unsigned(ILogger logger, string method);

    [LoggerMessage(EventId = 402, Level = LogLevel.Warning, Message = "Rejected an Inngest POST request without a body")]
    public static partial void MissingBody(ILogger logger);

    [LoggerMessage(EventId = 403, Level = LogLevel.Warning, Message = "Received an unknown Inngest probe {Probe}")]
    public static partial void UnknownProbe(ILogger logger, string probe);

    [LoggerMessage(EventId = 404, Level = LogLevel.Error, Message = "Inngest called function {FunctionId}, which this app does not have; is the app synced?")]
    public static partial void FunctionNotFound(ILogger logger, string functionId);

    [LoggerMessage(EventId = 405, Level = LogLevel.Error, Message = "Running function {FunctionId} failed")]
    public static partial void ExecutionFailed(ILogger logger, string functionId, Exception exception);

    [LoggerMessage(EventId = 406, Level = LogLevel.Information, Message = "Synced {FunctionCount} functions with Inngest at {ServeUrl}")]
    public static partial void Synced(ILogger logger, int functionCount, Uri serveUrl);

    [LoggerMessage(EventId = 407, Level = LogLevel.Debug, Message = "Synced with Inngest at {ServeUrl}; nothing changed")]
    public static partial void SyncUnchanged(ILogger logger, Uri serveUrl);

    [LoggerMessage(EventId = 408, Level = LogLevel.Error, Message = "Inngest rejected the sync with status {StatusCode}: {Error}")]
    public static partial void SyncRejected(ILogger logger, int statusCode, string? error);

    [LoggerMessage(EventId = 409, Level = LogLevel.Error, Message = "Syncing with Inngest failed")]
    public static partial void SyncFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 410, Level = LogLevel.Debug, Message = "Answered an in-band sync for {ServeUrl}")]
    public static partial void SyncedInBand(ILogger logger, Uri serveUrl);

    [LoggerMessage(EventId = 411, Level = LogLevel.Warning, Message = "Rejected an in-band sync: {Reason}")]
    public static partial void InvalidInBandSync(ILogger logger, string reason);

    [LoggerMessage(EventId = 412, Level = LogLevel.Warning, Message = "Received an Inngest {Method} request; only GET, PUT and POST are supported")]
    public static partial void MethodNotAllowed(ILogger logger, string method);

    [LoggerMessage(EventId = 413, Level = LogLevel.Error, Message = "Handling an Inngest {Method} request failed")]
    public static partial void RequestFailed(ILogger logger, string method, Exception exception);

    [LoggerMessage(EventId = 414, Level = LogLevel.Warning,
        Message = "An unsigned sync registered {ServeUrl}, taken from the request's Host header, which the caller chooses; set ServeOrigin, or INNGEST_SERVE_ORIGIN, to the app's public origin")]
    public static partial void UnsignedSyncFromRequestUrl(ILogger logger, Uri serveUrl);
}
