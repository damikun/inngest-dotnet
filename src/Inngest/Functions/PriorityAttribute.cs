// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

namespace Inngest;

/// <summary>
/// Sets FunctionOptions.Priority: Run is an expression returning a number of seconds between -600 and 600 that moves a
/// run forward or back in the queue.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class PriorityAttribute(string run) : FunctionOptionsAttribute
{
    public string Run { get; } = run;

    protected override FunctionOptions Configure(FunctionOptions options) => options with { Priority = new Priority(Run) };
}
