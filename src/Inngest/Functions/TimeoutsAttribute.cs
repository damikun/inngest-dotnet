// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

namespace Inngest;

/// <summary>Sets FunctionOptions.Timeouts. Start and Finish are time strings such as "10m" or "1h".</summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class TimeoutsAttribute : Attribute
{
    public string? Start { get; set; }

    public string? Finish { get; set; }
}
