// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using System.Diagnostics.CodeAnalysis;
using Inngest.OpenTelemetry.ExtendedTraces;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Inngest;

// The compiler does not flag a call to an extension method for the attribute on its class, so the method has its own.
[Experimental(ExtendedTracesDiagnostics.Id)]
public static class InngestExtendedTracesBuilderExtensions
{
    /// <summary>
    /// Sends the spans that the app's code makes while a function runs to Inngest, which shows them in the run's trace
    /// next to its steps: HTTP requests, database queries, model calls, from any ActivitySource. The spans go over
    /// OTLP to the Inngest API with the signing key, at the end of every call. Adds AddInstrumentation as well, whose
    /// spans for the call and its steps are the roots the others hang from. Extended traces are experimental in Inngest,
    /// so this API may still change.
    /// </summary>
    /// <remarks>
    /// The spans of a run are recorded even where the app's own sampler would drop them; other spans are left alone.
    /// A call continues the trace Inngest sent with it through the host's span for the request, which ASP.NET Core and
    /// the Connect worker start.
    /// </remarks>
    [Experimental(ExtendedTracesDiagnostics.Id)]
    public static IInngestBuilder AddExtendedTraces(this IInngestBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.TryAddSingleton<ExtendedTraceExporter>();
        return builder.AddInstrumentation();
    }
}

/// <summary>The diagnostic that marks the extended traces API as experimental.</summary>
public static class ExtendedTracesDiagnostics
{
    /// <summary>
    /// Suppress it with &lt;NoWarn&gt;$(NoWarn);INNGESTEXP002&lt;/NoWarn&gt; or #pragma warning disable INNGESTEXP002.
    /// </summary>
    public const string Id = "INNGESTEXP002";
}
