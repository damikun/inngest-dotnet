// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

namespace Inngest;

/// <summary>
/// Runs the function for every event with this name, optionally filtered by an expression such as
/// "event.data.total &gt; 100". A class can have several triggers, of either kind.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
public class EventTriggerAttribute(string name) : Attribute
{
    public string Event { get; } = name;

    public string? If { get; set; }
}

/// <summary>Runs the function for every event of type T, named T.EventName.</summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
public sealed class EventTriggerAttribute<T>() : EventTriggerAttribute(T.EventName)
    where T : IInngestEventData;
