// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.

using System.Diagnostics.CodeAnalysis;
using Inngest;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Microsoft.AspNetCore.Builder;

/// <summary>Maps the SignalR hub of Inngest realtime messages.</summary>
public static class InngestSignalREndpointRouteBuilderExtensions
{
    /// <summary>
    /// Maps <see cref="InngestRealtimeHub"/>, the SignalR hub whose clients join Inngest realtime messages with
    /// <c>Subscribe(channel, topics)</c>. Clients connect to it as to any hub; the endpoint follows the app's
    /// authorization, so add RequireAuthorization() to the result to require a signed-in user.
    /// </summary>
    public static HubEndpointConventionBuilder MapInngestRealtimeHub(this IEndpointRouteBuilder endpoints, [StringSyntax("Route")] string pattern)
    {
        Check(endpoints, pattern);
        return endpoints.MapHub<InngestRealtimeHub>(pattern);
    }

    /// <summary>
    /// Maps <see cref="InngestRealtimeHub"/> as <see cref="MapInngestRealtimeHub(IEndpointRouteBuilder, string)"/> does,
    /// with the options of the connections to it that <c>MapHub</c> takes, such as the transports and
    /// <c>CloseOnAuthenticationExpiration</c>, which ends the connection of a user whose authentication expires.
    /// </summary>
    public static HubEndpointConventionBuilder MapInngestRealtimeHub(
        this IEndpointRouteBuilder endpoints,
        [StringSyntax("Route")] string pattern,
        Action<HttpConnectionDispatcherOptions> configureOptions)
    {
        Check(endpoints, pattern);
        ArgumentNullException.ThrowIfNull(configureOptions);
        return endpoints.MapHub<InngestRealtimeHub>(pattern, configureOptions);
    }

    private static void Check(IEndpointRouteBuilder endpoints, string pattern)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentException.ThrowIfNullOrEmpty(pattern);

        // Resolving here fails at startup, not when the first client connects, when AddInngestSignalR was not called.
        _ = endpoints.ServiceProvider.GetService<InngestRealtimeBridge<InngestRealtimeHub>>()
            ?? throw new InvalidOperationException("Call services.AddInngestSignalR() before app.MapInngestRealtimeHub().");
    }
}
