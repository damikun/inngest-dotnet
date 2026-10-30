// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using Inngest.Internal;

namespace Inngest;

/// <summary>
/// The base of the trigger and flow control attributes. Derive from it to package options as an attribute of your
/// own, such as the concurrency and retries a team uses for every function that calls one API.
/// AddFunction&lt;TFunction&gt;() starts from [InngestFunction] and applies these attributes in ascending Order, and
/// those with the same Order in the order they are declared.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public abstract class FunctionOptionsAttribute : Attribute
{
    /// <summary>Lower values apply first, so a higher Order can replace what others set. The default is 0.</summary>
    public int Order { get; set; }

    /// <summary>Returns the options with this attribute applied.</summary>
    public FunctionOptions Apply(FunctionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        return Configure(options)
            ?? throw new InvalidOperationException(
                $"The Configure method of {GetType().Name} returned null instead of the function's options.");
    }

    /// <summary>Returns a copy of the options with what this attribute sets, such as options with { Retries = 5 }.</summary>
    protected abstract FunctionOptions Configure(FunctionOptions options);

    // Attribute arguments must be constants, so the built-in attributes take durations as time strings.
    private protected static TimeSpan Duration(FunctionOptions options, string attribute, string property, string? value) =>
        TimeString.TryParse(value, out var duration)
            ? duration
            : throw new InvalidOperationException(
                $"Function \"{options.Id}\": {property} of [{attribute}] is \"{value}\", which is not a time string. " +
                "Use whole numbers with w, d, h, m or s, such as \"30s\", \"5m\" or \"1h30m\".");

    private protected static TimeSpan? OptionalDuration(FunctionOptions options, string attribute, string property, string? value) =>
        value is null ? null : Duration(options, attribute, property, value);
}
