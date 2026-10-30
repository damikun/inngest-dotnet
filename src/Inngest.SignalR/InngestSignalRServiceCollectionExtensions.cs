// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using Inngest;
using Inngest.Realtime;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Microsoft.Extensions.DependencyInjection;

public static class InngestSignalRServiceCollectionExtensions
{
    /// <summary>
    /// Registers the bridge that forwards Inngest realtime messages to the clients of <see cref="InngestRealtimeHub"/>,
    /// which <c>MapInngestRealtimeHub</c> maps. Call AddSignalR and AddInngest as well; the order does not matter. The
    /// options must set <see cref="InngestSignalROptions.Authorize"/>.
    /// </summary>
    public static IServiceCollection AddInngestSignalR(this IServiceCollection services, Action<InngestSignalROptions> configure) =>
        services.AddInngestSignalR<InngestRealtimeHub>(configure);

    /// <summary>
    /// Registers the bridge for a hub of the app, which injects <see cref="InngestRealtimeBridge{THub}"/> and calls it
    /// from its own methods. Call AddSignalR and AddInngest as well; the order does not matter. The options must set
    /// <see cref="InngestSignalROptions.Authorize"/>, and are read now, so a mistake fails the app at startup.
    /// </summary>
    public static IServiceCollection AddInngestSignalR<THub>(this IServiceCollection services, Action<InngestSignalROptions> configure)
        where THub : Hub
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        var options = new InngestSignalROptions();
        configure(options);
        Validate(options);

        services.TryAddSingleton(provider => new InngestRealtimeBridge<THub>(
            provider.GetService<RealtimeSubscriber>()
                ?? throw new InvalidOperationException("Call services.AddInngest() before the Inngest SignalR bridge is used."),
            provider.GetRequiredService<IHubContext<THub>>(),
            provider.GetRequiredService<IServiceScopeFactory>(),
            options,
            (provider.GetService<ILoggerFactory>() ?? NullLoggerFactory.Instance).CreateLogger("Inngest.SignalR.InngestRealtimeBridge"),
            provider.GetService<IHostApplicationLifetime>()?.ApplicationStopping ?? CancellationToken.None));
        return services;
    }

    // Reports every problem at once, as the validators of the other packages do.
    private static void Validate(InngestSignalROptions options)
    {
        List<string> failures = [];
        if (options.Authorize is null)
        {
            failures.Add("InngestSignalROptions.Authorize is required, for example AddInngestSignalR(options => options.Authorize = request => ...). Return request.Requested to let everyone in.");
        }

        if (string.IsNullOrWhiteSpace(options.MessageMethod))
        {
            failures.Add("InngestSignalROptions.MessageMethod must not be empty.");
        }

        if (string.IsNullOrWhiteSpace(options.ErrorMethod))
        {
            failures.Add("InngestSignalROptions.ErrorMethod must not be empty.");
        }

        if (string.Equals(options.MessageMethod, options.ErrorMethod, StringComparison.OrdinalIgnoreCase))
        {
            failures.Add("InngestSignalROptions.MessageMethod and ErrorMethod must differ, or a client cannot tell messages from errors.");
        }

        if (options.MaxSubscriptionsPerConnection < 1)
        {
            failures.Add("InngestSignalROptions.MaxSubscriptionsPerConnection must be at least 1.");
        }

        if (failures.Count > 0)
        {
            throw new InvalidOperationException(string.Join(' ', failures));
        }
    }
}
