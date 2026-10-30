// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.

using Inngest.Configuration;
using Inngest.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Inngest;

public static class InngestEntityFrameworkBuilderExtensions
{
    /// <summary>
    /// Adds a transactional outbox for the DbContext TContext: events added with AddInngestEvent are saved in the same
    /// transaction as the rest of the unit of work, and a hosted dispatcher sends them to Inngest afterwards. Delivery
    /// is at least once: an event whose send is not recorded, for example because the process died in between, is sent
    /// again with the same id, which Inngest drops. The context needs the table
    /// (modelBuilder.AddInngestOutbox() in OnModelCreating, and a migration) and
    /// UseInngestOutbox(serviceProvider) in its options if saves are to wake the dispatcher at once instead of at the
    /// next poll. Every instance of the app runs a dispatcher; they share the table without a lock. Call it once for each
    /// DbContext that has events to send; each has its own table, settings and dispatcher.
    /// </summary>
    public static IInngestBuilder AddEntityFrameworkOutbox<TContext>(this IInngestBuilder builder, Action<InngestOutboxOptions>? configure = null)
        where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(builder);

        var services = builder.Services;
        var options = services.AddOptions<InngestOutboxOptions>(OutboxNames.For<TContext>());
        if (configure is not null)
        {
            options.Configure(configure);
        }

        // Fails the host at startup rather than in the first pass.
        options.ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<InngestOutboxOptions>, OutboxOptionsValidator>());

        services.TryAddSingleton<OutboxSignals>();
        services.TryAddSingleton<OutboxInterceptor>();
        services.TryAddSingleton(static provider => new OutboxEventWriter(
            provider.GetRequiredService<InngestSettings>().SerializerOptions,
            provider.GetRequiredService<TimeProvider>()));
        services.TryAddSingleton<OutboxProcessor<TContext>>();
        services.AddHostedService<OutboxDispatcher<TContext>>();
        return builder;
    }
}
