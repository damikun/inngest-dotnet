// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

namespace Inngest;

/// <summary>
/// An event to send. Data and User are serialized with InngestOptions.SerializerOptions using their runtime type.
/// </summary>
public sealed record InngestEvent(string Name, object? Data = null)
{
    /// <summary>Inngest drops a second event with the same id inside its deduplication window.</summary>
    public string? Id { get; init; }

    public object? User { get; init; }

    /// <summary>
    /// Defaults to the time Inngest receives the event. A future value delays functions that it triggers.
    /// </summary>
    public DateTimeOffset? Timestamp { get; init; }

    public string? Version { get; init; }

    /// <summary>The sessions the event belongs to. Sent from a run, it also inherits the run's sessions.</summary>
    public EventMeta? Meta { get; init; }

    internal static InngestEvent From<T>(T data)
        where T : IInngestEventData
    {
        ArgumentNullException.ThrowIfNull(data);
        return new InngestEvent(T.EventName, data) { Version = T.EventVersion };
    }
}
