// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using System.Diagnostics.CodeAnalysis;
using Inngest;
using Inngest.AspNetCore;

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
