// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

namespace Inngest;

/// <summary>
/// What to pass to AddSource and AddMeter when OpenTelemetry is set up without AddInngestInstrumentation, or to an
/// ActivityListener or MeterListener. The source is named like the TypeScript SDK's tracer.
/// </summary>
public static class InngestInstrumentation
{
    public const string ActivitySourceName = "inngest";

    public const string MeterName = "inngest";
}
