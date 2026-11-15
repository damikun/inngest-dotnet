// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

namespace Inngest;

/// <summary>
/// Raised when Inngest refuses events of a send and says which it took. Inngest goes on past a refused event and keeps
/// the others, which start their functions, so a send repeated as it was sends those again unless every event has an
/// Id. The message says how many Inngest accepted.
/// </summary>
public sealed class SendEventsException : InngestException
{
    /// <summary>Creates the exception for a send of as many events as there are ids.</summary>
    /// <param name="message">What went wrong.</param>
    /// <param name="statusCode">The status Inngest answered with.</param>
    /// <param name="ids">One id per event, in the order they were sent, with an empty string for each one refused.</param>
    public SendEventsException(string message, int statusCode, IReadOnlyList<string> ids)
        : base(message, statusCode)
    {
        ArgumentNullException.ThrowIfNull(ids);
        Ids = ids;
    }

    /// <summary>
    /// The id Inngest gave each event, in the order they were sent after any middleware, with an empty string for each
    /// event Inngest did not accept.
    /// </summary>
    public IReadOnlyList<string> Ids { get; }
}
