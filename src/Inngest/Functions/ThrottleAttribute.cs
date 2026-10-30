// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

namespace Inngest;

/// <summary>
/// Sets FunctionOptions.Throttle: runs beyond Limit per Period are queued, not dropped. Period is a time string such
/// as "1m".
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class ThrottleAttribute(int limit, string period) : Attribute
{
    public int Limit { get; } = limit;

    public string Period { get; } = period;

    public int Burst
    {
        get => BurstValue ?? 0;
        set => BurstValue = value;
    }

    public string? Key { get; set; }

    // An attribute property cannot be nullable, so this tells an unset Burst from 0.
    internal int? BurstValue { get; private set; }
}
