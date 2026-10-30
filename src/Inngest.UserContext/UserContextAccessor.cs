// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.

using System.Security.Claims;
using Inngest.Configuration;

namespace Inngest.UserContext;

// The user of one call from Inngest, scoped to it. The middleware sets it when the function starts; a service that was
// constructed before reads it through the same instance. It is what ICurrentUser and ClaimsPrincipal resolve to.
internal sealed class UserContextAccessor(UserContextCodec codec, InngestSettings settings) : ICurrentUser
{
    public const string AuthenticationType = "Inngest.UserContext";

    private State _state = State.Anonymous();

    public UserSnapshot? Snapshot => Volatile.Read(ref _state).Snapshot;

    public ClaimsPrincipal Principal => Volatile.Read(ref _state).Principal;

    public bool IsAuthenticated => Snapshot is not null;

    public DateTimeOffset? IssuedAt => Snapshot?.IssuedAt;

    public void Set(UserSnapshot? snapshot) =>
        Volatile.Write(ref _state, snapshot is null ? State.Anonymous() : new State(snapshot, CreatePrincipal(snapshot)));

    // The options of an invocation with the user of this run added under the reserved key, for the invoked function's
    // event. Unchanged without a user, or when the claims are larger than the bound.
    public InvokeOptions ForInvocation(InvokeOptions options)
    {
        if (Snapshot is not { } snapshot || codec.Protect(snapshot, SystemEvents.FunctionInvoked) is not { } protectedText)
        {
            return options;
        }

        return EventUsers.WithEnvelope(options.User, protectedText, settings.SerializerOptions) is { } user
            ? options with { User = user }
            : options;
    }

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

    private sealed record State(UserSnapshot? Snapshot, ClaimsPrincipal Principal)
    {
        // An identity without an authentication type is not authenticated.
        public static State Anonymous() => new(null, new ClaimsPrincipal(new ClaimsIdentity()));
    }
}
