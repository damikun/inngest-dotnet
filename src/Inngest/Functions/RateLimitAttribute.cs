// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

namespace Inngest;

/// <summary>
/// Sets FunctionOptions.RateLimit: runs beyond Limit per Period are dropped. Period is a time string such as "1h".
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class RateLimitAttribute(int limit, string period) : FunctionOptionsAttribute
{
    public int Limit { get; } = limit;

    public string Period { get; } = period;

    public string? Key { get; set; }

    protected override FunctionOptions Configure(FunctionOptions options) =>
        options with { RateLimit = new RateLimit(Limit, Duration(options, "RateLimit", "Period", Period)) { Key = Key } };
}
