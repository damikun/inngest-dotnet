// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using System.Diagnostics.CodeAnalysis;
using Inngest;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Microsoft.AspNetCore.Builder;

public static class InngestSignalREndpointRouteBuilderExtensions
{
    /// <summary>
    /// Maps <see cref="InngestRealtimeHub"/>, the SignalR hub whose clients join Inngest realtime messages with
    /// <c>Subscribe(channel, topics)</c>. Clients connect to it as to any hub; the endpoint follows the app's
    /// authorization, so add RequireAuthorization() to the result to require a signed-in user.
    /// </summary>
    public static HubEndpointConventionBuilder MapInngestRealtimeHub(this IEndpointRouteBuilder endpoints, [StringSyntax("Route")] string pattern)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentException.ThrowIfNullOrEmpty(pattern);

        // Resolving here fails at startup, not when the first client connects, when AddInngestSignalR was not called.
        _ = endpoints.ServiceProvider.GetService<InngestRealtimeBridge<InngestRealtimeHub>>()
            ?? throw new InvalidOperationException("Call services.AddInngestSignalR() before app.MapInngestRealtimeHub().");

        return endpoints.MapHub<InngestRealtimeHub>(pattern);
    }
}
