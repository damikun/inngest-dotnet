// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using Inngest;
using Inngest.Connect;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Microsoft.Extensions.DependencyInjection;

public static class InngestConnectHealthChecksBuilderExtensions
{
    // Adds a check that is healthy while the Connect worker of AddConnect holds an active connection, and reports
    // failureStatus (Unhealthy unless given) while it connects, reconnects or stops. It is meant for readiness probes:
    // tag it and keep it out of the liveness probe, because a reconnecting worker recovers on its own and a restart
    // would cancel the executions it is finishing.
    public static IHealthChecksBuilder AddInngestConnect(
        this IHealthChecksBuilder builder,
        string name = "inngest-connect",
        HealthStatus? failureStatus = null,
        IEnumerable<string>? tags = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);
        return builder.Add(new HealthCheckRegistration(
            name,
            static services => new ConnectHealthCheck(services.GetRequiredService<IInngestConnection>()),
            failureStatus,
            tags));
    }
}
