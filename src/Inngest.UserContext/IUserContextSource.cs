// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.

using System.Security.Claims;

namespace Inngest;

/// <summary>
/// Tells the user context who an event is sent for when an app sends it outside a run. Inngest.UserContext.AspNetCore
/// registers one that reads HttpContext.User; register your own for another kind of host, such as a SignalR hub or a
/// Blazor circuit. It is called for every send outside a run, so it should be cheap. Events sent from a run carry the
/// run's user instead, and never ask the source.
/// </summary>
public interface IUserContextSource
{
    /// <summary>
    /// The user the events being sent are for, or null for none. Only an authenticated user is attached, and only the
    /// claims that <see cref="UserContextOptions.Claims"/> allows.
    /// </summary>
    ValueTask<ClaimsPrincipal?> GetUserAsync(CancellationToken cancellationToken);
}
