// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using Inngest;

namespace OpenTelemetry.Trace;

public static class InngestInstrumentationTracerProviderBuilderExtensions
{
    // Collects the spans of the Inngest SDK: those of AddInstrumentation on the Inngest builder, and the Connect worker's
    // spans for the requests it receives.
    public static TracerProviderBuilder AddInngestInstrumentation(this TracerProviderBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.AddSource(InngestInstrumentation.ActivitySourceName);
    }
}
