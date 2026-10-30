// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

namespace Inngest;

/// <summary>
/// Match is a property path that must be equal on the triggering event and the awaited one, such as
/// "data.orderId". If is a full expression where "event" is the triggering event and "async" the awaited one.
/// </summary>
public sealed record WaitForEventOptions(string Event, TimeSpan Timeout)
{
    public string? Match { get; init; }

    public string? If { get; init; }
}
