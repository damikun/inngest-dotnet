// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.

using System.Security.Claims;

namespace Inngest;

/// <summary>
/// The user who caused the current run: the claims that were attached to the event when it was sent, restored for every
/// call of the run. It is a snapshot of the user as they were at send time, not a fresh authorization. The user may have
/// lost a role or been deleted since, and a run can start long after the send, so check permissions against current data
/// for anything that matters. It is scoped to one call from Inngest and is filled in when the function starts, so a
/// service that receives it through its constructor must read it when it uses it, not keep what it read there.
/// </summary>
public interface ICurrentUser
{
    /// <summary>
    /// The restored user: one identity with the claims the sender allowed, as strings, and the authentication type
    /// "Inngest.UserContext". Without a user, an anonymous principal with no claims whose identity is not authenticated.
    /// </summary>
    ClaimsPrincipal Principal { get; }

    /// <summary>True when the event carried a user that is valid for this run.</summary>
    bool IsAuthenticated { get; }

    /// <summary>When the sender took the claims, or null without a user.</summary>
    DateTimeOffset? IssuedAt { get; }
}
