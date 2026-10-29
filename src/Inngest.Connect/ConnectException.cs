// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

namespace Inngest.Connect;

internal enum ConnectFailure
{
    // No answer from Inngest or the gateway, or the socket broke during the handshake.
    Unreachable,

    // Inngest or the gateway answered, but not with what the protocol expects.
    Rejected,

    // The start request was refused with 401; the next attempt uses the other signing key.
    Unauthorized,

    // The account has as many connections as it may have.
    ConnectionLimit,

    // The gateway is draining and turned the connection away; another one takes over shortly.
    GatewayClosing,

    // The gateway could not sync the app, usually because of its configuration.
    SyncFailed,
}

// Why one attempt to connect failed. The worker keeps trying, so this never reaches the app.
internal sealed class ConnectException(ConnectFailure failure, string message, Exception? innerException = null)
    : Exception(message, innerException)
{
    public ConnectFailure Failure { get; } = failure;
}
