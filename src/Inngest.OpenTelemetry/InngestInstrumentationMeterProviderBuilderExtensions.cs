// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using Inngest;

namespace OpenTelemetry.Metrics;

public static class InngestInstrumentationMeterProviderBuilderExtensions
{
    /// <summary>Collects the metrics of AddInstrumentation on the Inngest builder.</summary>
    public static MeterProviderBuilder AddInngestInstrumentation(this MeterProviderBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.AddMeter(InngestInstrumentation.MeterName);
    }
}
