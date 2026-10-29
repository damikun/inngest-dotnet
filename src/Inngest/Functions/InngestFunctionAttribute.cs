// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

namespace Inngest;

// Configures a function class for AddFunction<TFunction>(). Its members, and the trigger and flow control attributes
// next to it, each set the FunctionOptions member of the same name. Durations are time strings such as "30s", "5m"
// or "1h30m", because attribute arguments must be constants.
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class InngestFunctionAttribute(string id) : Attribute
{
    // Unique within the app. Inngest addresses the function as "<app id>-<id>".
    public string Id { get; } = id;

    public string? Name { get; set; }

    // Retries after the first attempt, from 0 to 20. Inngest uses its default when this is not set.
    public int Retries
    {
        get => RetriesValue ?? 0;
        set => RetriesValue = value;
    }

    // An expression whose value deduplicates runs for 24 hours, for example "event.data.orderId".
    public string? Idempotency { get; set; }

    // An attribute property cannot be nullable, so this tells an unset Retries from 0.
    internal int? RetriesValue { get; private set; }
}
