// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

namespace Inngest;

/// <summary>
/// Configures a function class for AddFunction&lt;TFunction&gt;(). Its members, and the trigger and flow control
/// attributes next to it, each set the FunctionOptions member of the same name; those attributes derive from
/// FunctionOptionsAttribute and apply after it. Durations are time strings such as "30s", "5m" or "1h30m", because
/// attribute arguments must be constants.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class InngestFunctionAttribute(string id) : Attribute
{
    /// <summary>Unique within the app. Inngest addresses the function as "&lt;app id&gt;-&lt;id&gt;".</summary>
    public string Id { get; } = id;

    public string? Name { get; set; }

    /// <summary>Retries after the first attempt, from 0 to 20. Inngest uses its default when this is not set.</summary>
    public int Retries
    {
        get => RetriesValue ?? 0;
        set => RetriesValue = value;
    }

    /// <summary>An expression whose value deduplicates runs for 24 hours, for example "event.data.orderId".</summary>
    public string? Idempotency { get; set; }

    // An attribute property cannot be nullable, so this tells an unset Retries from 0.
    internal int? RetriesValue { get; private set; }
}
