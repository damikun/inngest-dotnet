// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using System.Diagnostics.CodeAnalysis;
using Inngest;
using Inngest.AspNetCore;
using Inngest.Realtime;
using Inngest.Serving;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Microsoft.AspNetCore.Builder;

public static class InngestEndpointRouteBuilderExtensions
{
    /// <summary>
    /// Serves the Inngest endpoint: GET for introspection, PUT to sync the app and POST to run functions.
    /// </summary>
    public static IEndpointConventionBuilder MapInngest(this IEndpointRouteBuilder endpoints, [StringSyntax("Route")] string pattern = "/api/inngest")
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentException.ThrowIfNullOrEmpty(pattern);

        // Resolving here fails at startup, not on the first request, when AddInngest was not called.
        var handler = endpoints.ServiceProvider.GetService<InngestCommHandler>()
            ?? throw new InvalidOperationException("Call services.AddInngest() before app.MapInngest().");

        // Inngest proves who it is with the request signature, which the SDK checks, and sends nothing ASP.NET Core
        // authorization could check. Without AllowAnonymous an app's fallback policy would turn every call away.
        return endpoints
            .MapMethods(pattern, [HttpMethods.Get, HttpMethods.Put, HttpMethods.Post], context => InngestEndpoint.HandleAsync(context, handler))
            .WithDisplayName("Inngest")
            .ExcludeFromDescription()
            .AllowAnonymous();
    }

    /// <summary>
    /// Serves realtime messages to browsers as server-sent events, which an EventSource reads without an Inngest
    /// client: each event's data is a message as Inngest sends it. The callback says what a request may receive, from
    /// its route values and its user, or returns null to refuse it with 403. Requests for the same topics of a channel
    /// share one subscription to Inngest. The endpoint follows the app's authorization; add RequireAuthorization() to
    /// the result to require a signed-in user.
    /// </summary>
    public static IEndpointConventionBuilder MapInngestRealtime(
        this IEndpointRouteBuilder endpoints,
        [StringSyntax("Route")] string pattern,
        Func<HttpContext, ValueTask<RealtimeSubscription?>> subscription)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentException.ThrowIfNullOrEmpty(pattern);
        ArgumentNullException.ThrowIfNull(subscription);

        var services = endpoints.ServiceProvider;
        var subscriber = services.GetService<RealtimeSubscriber>()
            ?? throw new InvalidOperationException("Call services.AddInngest() before app.MapInngestRealtime().");
        var relay = new RealtimeRelay(
            subscriber,
            services.GetService<TimeProvider>() ?? TimeProvider.System,
            (services.GetService<ILoggerFactory>() ?? NullLoggerFactory.Instance).CreateLogger("Inngest.AspNetCore.RealtimeRelay"),
            services.GetService<IHostApplicationLifetime>()?.ApplicationStopping ?? CancellationToken.None);

        return endpoints
            .MapGet(pattern, (RequestDelegate)(context => relay.ServeAsync(context, subscription)))
            .WithDisplayName("Inngest realtime")
            .ExcludeFromDescription();
    }
}
