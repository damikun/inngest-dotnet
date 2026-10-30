// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.

using System.Security.Claims;
using Inngest.UserContext;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Inngest;

public static class InngestUserContextBuilderExtensions
{
    /// <summary>
    /// Carries the user who caused a run into it. The middleware restores the user of the triggering event as
    /// <see cref="ICurrentUser"/> and as a scoped ClaimsPrincipal, and attaches the user to the events that an app
    /// sends: the user a <see cref="IUserContextSource"/> names for sends outside a run (Inngest.UserContext.AspNetCore
    /// reads HttpContext.User), and the run's own for the events a run sends. Only the claims of
    /// <see cref="UserContextOptions.Claims"/> are attached, protected so that nobody can read or forge them: with ASP.NET
    /// Core Data Protection unless an <see cref="IUserContextProtector"/> is registered. Data Protection has to be set
    /// up by the app with <c>AddDataProtection</c>, and every app that sends or restores users needs the same key ring and
    /// application name. Register this before middleware that needs the user. Options can also be set with
    /// <c>Configure&lt;UserContextOptions&gt;</c>.
    /// </summary>
    public static IInngestBuilder AddUserContext(this IInngestBuilder builder, Action<UserContextOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var services = builder.Services;
        var options = services.AddOptions<UserContextOptions>();
        if (configure is not null)
        {
            options.Configure(configure);
        }

        // Fails the host at startup rather than on the first send or call.
        options.ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<UserContextOptions>, UserContextOptionsValidator>());

        // Data Protection is not added here: its key store and application name are what makes a payload readable by
        // another app, so the app sets them up where it can see them.
        services.TryAddSingleton<IUserContextProtector>(static provider => new DataProtectionUserContextProtector(
            provider.GetService<IDataProtectionProvider>() ?? throw new InvalidOperationException(
                "The user context protects users with ASP.NET Core Data Protection, which is not set up. Call services.AddDataProtection() " +
                "with a key store and application name that the apps sending and restoring users share, or register an IUserContextProtector.")));
        services.TryAddSingleton<UserContextCodec>();

        // The current user looks the run up from the async flow, so it is the same in every scope of the run, the scope
        // of a scoped step included.
        services.TryAddSingleton<ICurrentUser, CurrentUser>();

        // Resolves to the user as it is when the service is created, which is anonymous before the function starts.
        services.TryAddScoped<ClaimsPrincipal>(static provider => provider.GetRequiredService<ICurrentUser>().Principal);
        return builder.AddMiddleware<UserContextMiddleware>();
    }
}
