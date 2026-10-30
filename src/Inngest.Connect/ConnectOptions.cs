// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

namespace Inngest;

/// <summary>
/// Settings of the Connect worker. The app id, keys, environment and Inngest address come from InngestOptions;
/// settings left null here fall back to the INNGEST_CONNECT_* environment variables.
/// </summary>
public sealed class ConnectOptions
{
    /// <summary>
    /// Identifies this worker to Inngest, which tracks leases and capacity per instance. Keep it stable across
    /// reconnects and restarts of the same worker, and unique between workers. Defaults to the machine name.
    /// </summary>
    public string? InstanceId { get; set; }

    /// <summary>
    /// The most executions Inngest runs on this worker at the same time; Inngest enforces it. Null reads
    /// INNGEST_CONNECT_MAX_WORKER_CONCURRENCY and otherwise leaves it unlimited.
    /// </summary>
    public int? MaxWorkerConcurrency { get; set; }

    /// <summary>
    /// A ws:// or wss:// URL to dial instead of the gateway Inngest assigns, for example when a proxy or port mapping
    /// sits in between. Null reads INNGEST_CONNECT_GATEWAY_URL.
    /// </summary>
    public Uri? GatewayUrl { get; set; }
}
