// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

namespace Inngest;

/// <summary>
/// Adds a CancelOn to FunctionOptions.CancelOn: a running function is cancelled when a matching event arrives. Repeat
/// it for every event that cancels the function. Timeout is a time string such as "1h".
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
public class CancelOnAttribute(string name) : FunctionOptionsAttribute
{
    public string Event { get; } = name;

    public string? If { get; set; }

    /// <summary>
    /// A property path compared between the triggering event and the cancelling one, such as "data.userId".
    /// </summary>
    // Named like CancelOn.Match. It hides Attribute.Match, which compares attributes and is of no use here.
    public new string? Match { get; set; }

    public string? Timeout { get; set; }

    protected override FunctionOptions Configure(FunctionOptions options) =>
        options with
        {
            CancelOn =
            [
                .. options.CancelOn ?? [],
                new CancelOn(Event) { If = If, Match = Match, Timeout = OptionalDuration(options, "CancelOn", "Timeout", Timeout) },
            ],
        };
}

/// <summary>Cancels the function when an event of type T, named T.EventName, arrives.</summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
public sealed class CancelOnAttribute<T>() : CancelOnAttribute(T.EventName)
    where T : IInngestEventData;
