// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.

using System.Security.Claims;

namespace Inngest.UserContext;

// ICurrentUser as the app gets it. It holds no user itself: every read asks for the user of the run whose code is
// running, so it is the same instance in every scope and answers the same in all of them.
internal sealed class CurrentUser : ICurrentUser
{
    public ClaimsPrincipal Principal => RunUsers.Current.Principal;

    public bool IsAuthenticated => RunUsers.Current.Snapshot is not null;

    public DateTimeOffset? IssuedAt => RunUsers.Current.Snapshot?.IssuedAt;
}
