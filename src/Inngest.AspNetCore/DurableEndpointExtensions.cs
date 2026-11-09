// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using System.Diagnostics.CodeAnalysis;
using Inngest;
using Inngest.AspNetCore;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Microsoft.AspNetCore.Builder;

/// <summary>Durable endpoints: HTTP endpoints whose handlers run steps, as an Inngest function does.</summary>
// The compiler does not flag a call to an extension method for the attribute on its class, so each method has its own.
[Experimental(DurableEndpointDiagnostics.Id)]
public static class DurableEndpointExtensions
{
    /// <summary>
    /// Makes the endpoint durable: each request becomes a run, and the handler can take IStepTools and run steps. The
    /// client gets the handler's response; when a step has to wait or fails, Inngest finishes the run and the client
    /// is sent to wait for the response. Requires app.UseInngestDurableEndpoints().
    /// </summary>
    [Experimental(DurableEndpointDiagnostics.Id)]
    public static TBuilder WithDurableEndpoint<TBuilder>(this TBuilder builder, DurableEndpointOptions? options = null)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);
        var metadata = new DurableEndpointMetadata(options ?? new DurableEndpointOptions());
        builder.Add(endpoint => endpoint.Metadata.Add(metadata));
        return builder;
    }

    /// <summary>Makes the endpoint durable under the given function id; see the overload with options.</summary>
    [Experimental(DurableEndpointDiagnostics.Id)]
    public static TBuilder WithDurableEndpoint<TBuilder>(this TBuilder builder, string id)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        return builder.WithDurableEndpoint(new DurableEndpointOptions { Id = id });
    }

    /// <summary>
    /// Serves the output of durable endpoint runs on the app's own domain, as endpointProxy() does in the TypeScript SDK:
    /// a GET with the runId and token query parameters waits for the run to end and answers with the endpoint's
    /// response, which it fetches from Inngest with the run's token. It answers any origin, with CORS headers and an
    /// OPTIONS preflight, and decrypts the output when the app uses the encryption middleware, which Inngest cannot
    /// replay. Point DurableEndpointOptions.RedirectUrl at it. Requires services.AddInngest().
    /// </summary>
    [Experimental(DurableEndpointDiagnostics.Id)]
    public static IEndpointConventionBuilder MapInngestDurableEndpointProxy(this IEndpointRouteBuilder endpoints, [StringSyntax("Route")] string pattern)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentException.ThrowIfNullOrEmpty(pattern);

        // Resolved here, so that a missing AddInngest fails at startup rather than on the first request.
        var proxy = DurableEndpointProxy.Create(endpoints.ServiceProvider);

        // The run's token is what lets a caller read the output, as on Inngest's own URL.
        return endpoints
            .MapMethods(pattern, [HttpMethods.Get, HttpMethods.Options], proxy.HandleAsync)
            .WithDisplayName("Inngest durable endpoint output")
            .ExcludeFromDescription()
            .AllowAnonymous();
    }

    /// <summary>
    /// Runs the endpoints marked with WithDurableEndpoint() or [DurableEndpoint] as durable endpoints. Add it after
    /// routing, which WebApplication adds on its own. Everything after it in the pipeline runs inside the run, again
    /// for each call with which Inngest resumes it; those calls carry the original body and query, but not the
    /// original headers or user. Requires services.AddInngest().
    /// </summary>
    [Experimental(DurableEndpointDiagnostics.Id)]
    public static IApplicationBuilder UseInngestDurableEndpoints(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.UseMiddleware<DurableEndpointMiddleware>();
    }
}
