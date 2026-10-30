// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.

using Inngest.UserContext.AspNetCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Inngest;

public static class InngestUserContextAspNetCoreBuilderExtensions
{
    /// <summary>
    /// Makes the user of the HTTP request the user of the events that the app sends while serving it, so that a run
    /// started by a click knows who clicked. The principal is the one authentication put in HttpContext.User, and only
    /// an authenticated one is used. Events sent from a run never use it: the request that called the app is Inngest's,
    /// not a user's. Needs <c>AddUserContext</c>; does nothing when the app registered its own
    /// <see cref="IUserContextSource"/> first.
    /// </summary>
    public static IInngestBuilder AddUserContextFromHttpContext(this IInngestBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.AddHttpContextAccessor();
        builder.Services.TryAddSingleton<IUserContextSource, HttpContextUserContextSource>();
        return builder;
    }
}
