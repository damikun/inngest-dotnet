// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

namespace Inngest;

/// <summary>
/// Puts the event's name on the type of its data, so that code sends, triggers on and waits for the type instead of
/// repeating a string that can drift from the data it carries. The members are static, so reading them needs no
/// reflection.
/// </summary>
public interface IInngestEventData
{
    /// <summary>The name the event is sent and triggered under, such as "shop/order.paid".</summary>
    static abstract string EventName { get; }

    /// <summary>The version sent with the event, or null to send none.</summary>
    static virtual string? EventVersion => null;
}
