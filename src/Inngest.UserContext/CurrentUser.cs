// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.

using System.Security.Claims;
using Inngest.Configuration;

namespace Inngest.UserContext;

// ICurrentUser as the app gets it. It holds no user itself: every read asks for the user of the run whose code is
// running, so it is the same instance in every scope and answers the same in all of them.
internal sealed class CurrentUser(UserContextCodec codec, InngestSettings settings) : ICurrentUser
{
    public ClaimsPrincipal Principal => RunUsers.Current.Principal;

    public bool IsAuthenticated => RunUsers.Current.Snapshot is not null;

    public DateTimeOffset? IssuedAt => RunUsers.Current.Snapshot?.IssuedAt;

    // The options of an invocation with the user of the running run added under the reserved key, for the invoked
    // function's event. Unchanged without a user, or when the claims are larger than the bound.
    public InvokeOptions ForInvocation(InvokeOptions options)
    {
        if (RunUsers.Current.Snapshot is not { } snapshot || codec.Protect(snapshot, SystemEvents.FunctionInvoked) is not { } protectedText)
        {
            return options;
        }

        return EventUsers.WithEnvelope(options.User, protectedText, settings.SerializerOptions) is { } user
            ? options with { User = user }
            : options;
    }
}
