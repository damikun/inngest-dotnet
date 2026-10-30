// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

namespace Inngest;

public enum InngestConnectionState
{
    /// <summary>Not connected yet: the worker is opening its first connection, or retrying it.</summary>
    Connecting,

    /// <summary>Connected, and Inngest sends executions to this worker.</summary>
    Active,

    /// <summary>
    /// The connection broke or its gateway is draining. Executions already running go on, and a new connection is
    /// being opened.
    /// </summary>
    Reconnecting,

    /// <summary>The host is stopping. Inngest sends no new executions, and the running ones finish.</summary>
    Closing,

    /// <summary>The worker has stopped.</summary>
    Closed,
}
