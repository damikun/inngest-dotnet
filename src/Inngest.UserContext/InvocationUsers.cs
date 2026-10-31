// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.

using Inngest.Configuration;

namespace Inngest.UserContext;

// Puts the user of a run into the options of an invocation. An invocation passes through no middleware, so the code
// asks for it with InvokeOptions.WithUser.
internal sealed class InvocationUsers(UserContextCodec codec, InngestSettings settings)
{
    // The options with the user added under the reserved key, for the invoked function's event. Unchanged without a user,
    // or when the claims are larger than the bound. The text is made for the function, which Inngest addresses as
    // "<app id>-<function id>", the app being this one unless the options name another.
    public InvokeOptions ForInvocation(InvokeOptions options, UserSnapshot? snapshot)
    {
        var function = $"{(string.IsNullOrEmpty(options.AppId) ? settings.AppId : options.AppId)}-{options.FunctionId}";
        if (snapshot is null || codec.Protect(snapshot, SystemEvents.FunctionInvoked, function) is not { } protectedText)
        {
            return options;
        }

        return EventUsers.WithEnvelope(options.User, protectedText, settings.SerializerOptions) is { } user
            ? options with { User = user }
            : options;
    }
}
