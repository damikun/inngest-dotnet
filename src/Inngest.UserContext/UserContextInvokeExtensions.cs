// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.

using Inngest.UserContext;

namespace Inngest;

public static class UserContextInvokeExtensions
{
    /// <summary>
    /// Passes the user of the run to the function it invokes, which receives it as its own <see cref="ICurrentUser"/>.
    /// Invocations do not pass through middleware, so unlike events they do not carry the user unless the code asks. The
    /// user keeps its original expiry, and other keys in <see cref="InvokeOptions.User"/> are kept. Returns the options
    /// as they are when the run has no user.
    /// </summary>
    public static InvokeOptions WithUser(this InvokeOptions options, ICurrentUser user)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(user);

        // A fake in a unit test has nothing to protect.
        return user is UserContextAccessor accessor ? accessor.ForInvocation(options) : options;
    }
}
