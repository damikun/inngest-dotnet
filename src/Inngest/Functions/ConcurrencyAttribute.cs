// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

namespace Inngest;

/// <summary>
/// Adds a ConcurrencyLimit to FunctionOptions.Concurrency: how many steps of matching runs execute at once. Inngest
/// allows two limits, so the attribute can be repeated.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
public sealed class ConcurrencyAttribute(int limit) : Attribute
{
    public int Limit { get; } = limit;

    public string? Key { get; set; }

    public ConcurrencyScope Scope { get; set; } = ConcurrencyScope.Function;
}
