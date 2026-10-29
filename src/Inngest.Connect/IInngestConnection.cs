// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

namespace Inngest;

// The Connect worker's connection to Inngest, which the TypeScript SDK calls WorkerConnection. AddConnect registers it,
// so app code and probes can tell a worker that takes work from one that has lost its connection.
public interface IInngestConnection
{
    /// <summary>The state of the connection now.</summary>
    InngestConnectionState State { get; }

    /// <summary>Completes with the state the connection moves to once it is no longer in the given state.</summary>
    Task<InngestConnectionState> WaitForStateChangedAsync(InngestConnectionState lastObservedState, CancellationToken cancellationToken = default);
}

public enum InngestConnectionState
{
    // Not connected yet: the worker is opening its first connection, or retrying it.
    Connecting,

    // Connected, and Inngest sends executions to this worker.
    Active,

    // The connection broke or its gateway is draining. Executions already running go on, and a new connection is
    // being opened.
    Reconnecting,

    // The host is stopping. Inngest sends no new executions, and the running ones finish.
    Closing,

    // The worker has stopped.
    Closed,
}
