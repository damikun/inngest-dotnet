// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using Inngest.OpenTelemetry;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Inngest;

public static class InngestOpenTelemetryBuilderExtensions
{
    // Adds the middleware that traces and measures runs: a span for every call from Inngest and for every step the app
    // executes, and metrics for runs, steps, retries and durations. Add it before other middleware so that their work
    // falls inside its spans, and export it with AddInngestInstrumentation on the OpenTelemetry builders.
    public static IInngestBuilder AddInstrumentation(this IInngestBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.TryAddSingleton<InngestTelemetry>();
        return builder.AddMiddleware<InstrumentationMiddleware>();
    }
}
