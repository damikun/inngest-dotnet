// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.

using System.Security.Claims;

namespace Inngest.UserContext;

// The user of one call of a run: the snapshot that was restored, or none, and the principal made from it.
internal sealed class RunUser
{
    public const string AuthenticationType = "Inngest.UserContext";

    public RunUser(UserSnapshot? snapshot)
    {
        Snapshot = snapshot;

        // An identity without an authentication type is not authenticated.
        Principal = snapshot is null ? new ClaimsPrincipal(new ClaimsIdentity()) : CreatePrincipal(snapshot);
    }

    public UserSnapshot? Snapshot { get; }

    public ClaimsPrincipal Principal { get; }

    private static ClaimsPrincipal CreatePrincipal(UserSnapshot snapshot)
    {
        var claims = new List<Claim>();
        foreach (var (type, values) in snapshot.Claims)
        {
            foreach (var value in values)
            {
                claims.Add(new Claim(type, value));
            }
        }

        return new ClaimsPrincipal(new ClaimsIdentity(
            claims,
            AuthenticationType,
            snapshot.NameClaimType ?? ClaimTypes.Name,
            snapshot.RoleClaimType ?? ClaimTypes.Role));
    }
}
