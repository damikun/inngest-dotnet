// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.

using Inngest.UserContext;
using Microsoft.Extensions.DependencyInjection;

namespace Inngest;

public static class UserContextInvokeExtensions
{
    /// <summary>
    /// Passes the user of the run to the function it invokes, which receives it as its own <see cref="ICurrentUser"/>.
    /// Invocations do not pass through middleware, so unlike events they carry the user only when the code asks. The user
    /// keeps its original expiry, is made for the function that is invoked, and other keys in
    /// <see cref="InvokeOptions.User"/> are kept. Returns the options as they are when the run has no user. Throws when
    /// <c>AddUserContext</c> has not been called.
    /// </summary>
    public static InvokeOptions WithUser(this InvokeOptions options, FunctionContext context)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(context);

        var users = context.Services.GetService<InvocationUsers>() ??
            throw new InvalidOperationException("The user context is not set up, so the user cannot be passed to an invocation. Call AddUserContext on the Inngest builder.");
        return users.ForInvocation(options, RunUsers.Of(context).Snapshot);
    }
}
