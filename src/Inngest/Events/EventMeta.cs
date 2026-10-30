// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

namespace Inngest;

/// <summary>What an event carries beside its data: the sessions Inngest groups runs by.</summary>
public sealed record EventMeta
{
    /// <summary>
    /// The sessions the event belongs to, by session key and id, such as "ticket_id" and "ticket_42". An event can
    /// carry five. These win over the sessions it inherits from the run that sends it, and a null id drops the
    /// inherited session of that key. On a received event, the sessions Inngest recorded for it.
    /// </summary>
    public IReadOnlyDictionary<string, string?>? Sessions { get; init; }

    /// <summary>
    /// The sessions the event inherits from the run that sends it. The SDK fills them in from the run's
    /// FunctionContext.Sessions unless they are already set, InheritSessions is false or
    /// InngestOptions.SessionPropagation is off; middleware may change them.
    /// </summary>
    public IReadOnlyDictionary<string, string>? PropagatedSessions { get; init; }

    /// <summary>False sends the event without the sessions of the run that sends it.</summary>
    public bool InheritSessions { get; init; } = true;
}
