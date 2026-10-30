// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.

namespace Inngest;

/// <summary>
/// One event in the outbox table. AddInngestEvent creates it in the DbContext's unit of work, so it is saved or lost
/// together with the app's data, and the dispatcher sends it to Inngest afterwards and keeps track of how that went.
/// All times are UTC.
/// </summary>
public sealed class InngestOutboxMessage
{
    /// <summary>
    /// The id the event is sent with, on every attempt. Inngest starts the functions of an event once per id, so a
    /// resend after a crash between sending and marking the row creates no second run. Unique in the table.
    /// </summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>The event name, such as "shop/order.placed".</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>The event's data as a JSON object, serialized with the SDK's serializer options when the event was added.</summary>
    public string Data { get; set; } = "{}";

    /// <summary>The event's user data as a JSON object, or null.</summary>
    public string? User { get; set; }

    /// <summary>The version of the event's schema, sent as "v", or null.</summary>
    public string? Version { get; set; }

    /// <summary>The event's sessions as a JSON object of session key and id, or null.</summary>
    public string? Sessions { get; set; }

    /// <summary>
    /// When the event happened, sent as its "ts": the time it was added, unless the event carried one. Not when it was
    /// sent, which can be much later if Inngest was out of reach.
    /// </summary>
    public DateTime Timestamp { get; set; }

    /// <summary>The earliest time the next attempt to send the event may start; its first attempt is due as soon as it is saved.</summary>
    public DateTime NextAttemptAt { get; set; }

    /// <summary>How many times sending the event was attempted, the one that worked included.</summary>
    public int Attempts { get; set; }

    /// <summary>Why the last attempt failed, or null if it did not or there was none.</summary>
    public string? LastError { get; set; }

    /// <summary>
    /// Set by the dispatcher that claimed the event, to a value it made up for this claim. Only the dispatcher holding
    /// the token marks the event as sent or failed.
    /// </summary>
    public string? LeaseToken { get; set; }

    /// <summary>
    /// Until when the claim holds. A dispatcher that crashed holds its events until then, and any dispatcher claims
    /// them again afterwards.
    /// </summary>
    public DateTime? LeaseExpiresAt { get; set; }

    /// <summary>When Inngest accepted the event, or null while it is still waiting. Sent events are deleted after the retention time.</summary>
    public DateTime? SentAt { get; set; }
}
