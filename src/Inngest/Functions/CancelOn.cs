// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

namespace Inngest;

/// <summary>
/// Cancels a running function when a matching event arrives. Match is a property path compared between the
/// triggering event and the cancelling one, such as "data.userId".
/// </summary>
public sealed record CancelOn(string Event)
{
    public string? If { get; init; }

    public string? Match { get; init; }

    public TimeSpan? Timeout { get; init; }

    /// <summary>
    /// Set the other members with a with expression, for example CancelOn.For&lt;UserDeleted&gt;() with { Match =
    /// "data.userId" }.
    /// </summary>
    public static CancelOn For<T>()
        where T : IInngestEventData => new(T.EventName);
}
