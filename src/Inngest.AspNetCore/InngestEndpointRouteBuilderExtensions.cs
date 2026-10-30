// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using System.Diagnostics.CodeAnalysis;
using Inngest.AspNetCore;
using Inngest.Serving;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

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
}
