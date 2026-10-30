// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

namespace Inngest;

/// <summary>
/// Sets FunctionOptions.Debounce: a run waits until no new matching event arrives for Period, then runs once with the
/// last event. Period and Timeout are time strings such as "30s".
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class DebounceAttribute(string period) : FunctionOptionsAttribute
{
    public string Period { get; } = period;

    public string? Key { get; set; }

    public string? Timeout { get; set; }

    protected override FunctionOptions Configure(FunctionOptions options) =>
        options with
        {
            Debounce = new Debounce(Duration(options, "Debounce", "Period", Period))
            {
                Key = Key,
                Timeout = OptionalDuration(options, "Debounce", "Timeout", Timeout),
            },
        };
}
