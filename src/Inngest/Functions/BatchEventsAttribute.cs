// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

namespace Inngest;

/// <summary>
/// Sets FunctionOptions.BatchEvents: the function runs with up to MaxSize events at once, or with whatever arrived once
/// Timeout, a time string such as "5s", passes.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class BatchEventsAttribute(int maxSize, string timeout) : Attribute
{
    public int MaxSize { get; } = maxSize;

    public string Timeout { get; } = timeout;

    public string? Key { get; set; }

    public string? If { get; set; }
}
