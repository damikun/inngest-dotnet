// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Inngest.Connect;

// Healthy only while Inngest can send the worker executions, which is what a readiness probe asks: a rolling deployment
// then waits for the new worker's connection before it stops the old one.
internal sealed class ConnectHealthCheck(IInngestConnection connection) : IHealthCheck
{
    private static readonly IReadOnlyDictionary<string, object>[] Data =
    [
        .. Enum.GetValues<InngestConnectionState>().Select(static state => new Dictionary<string, object> { ["state"] = state.ToString() }),
    ];

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var state = connection.State;
        var data = Data[(int)state];
        return Task.FromResult(state == InngestConnectionState.Active
            ? HealthCheckResult.Healthy("Connected to Inngest.", data)
            : new HealthCheckResult(context.Registration.FailureStatus, Describe(state), data: data));
    }

    private static string Describe(InngestConnectionState state) => state switch
    {
        InngestConnectionState.Connecting => "Not connected to Inngest yet; failed attempts are in the worker's log.",
        InngestConnectionState.Reconnecting => "Reconnecting to Inngest; executions already running go on.",
        InngestConnectionState.Closing => "The worker is stopping and takes no new executions.",
        _ => "The worker has stopped.",
    };
}
