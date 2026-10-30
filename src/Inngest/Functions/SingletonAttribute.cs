// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

namespace Inngest;

/// <summary>Sets FunctionOptions.Singleton: only one run per Key is active at a time.</summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class SingletonAttribute(SingletonMode mode) : Attribute
{
    public SingletonMode Mode { get; } = mode;

    public string? Key { get; set; }
}
