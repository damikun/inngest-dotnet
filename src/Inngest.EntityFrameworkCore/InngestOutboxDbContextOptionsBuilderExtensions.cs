// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.

using Inngest.EntityFrameworkCore;

namespace Microsoft.EntityFrameworkCore;

public static class InngestOutboxDbContextOptionsBuilderExtensions
{
    /// <summary>
    /// Lets saves wake the dispatcher: the dispatcher looks at the table as soon as a save that added events has
    /// committed, instead of at the next poll. Without it events are still sent, up to one poll interval later. It works
    /// wherever the options of the context are built, AddDbContext and AddDbContextPool with or without a service
    /// provider, AddDbContextFactory and OnConfiguring, because it finds the dispatcher through the application's services
    /// that those leave in the options. When the save is inside a transaction of the code's own, the wake-up comes with
    /// its commit. With a TransactionScope it comes at the save, before the scope completes, and the events leave at the
    /// next poll.
    /// </summary>
    public static DbContextOptionsBuilder UseInngestOutbox(this DbContextOptionsBuilder optionsBuilder)
    {
        ArgumentNullException.ThrowIfNull(optionsBuilder);
        return optionsBuilder.AddInterceptors(OutboxInterceptor.Instance);
    }

    /// <summary>
    /// The same, and it also fails when the options are built if AddEntityFrameworkOutbox has not been called on the
    /// Inngest builder, instead of when the first event is added. Pass the provider of the service container, for example
    /// AddDbContext&lt;ShopContext&gt;((services, options) =&gt; options.UseNpgsql(connectionString).UseInngestOutbox(services)).
    /// </summary>
    public static DbContextOptionsBuilder UseInngestOutbox(this DbContextOptionsBuilder optionsBuilder, IServiceProvider serviceProvider)
    {
        ArgumentNullException.ThrowIfNull(optionsBuilder);
        ArgumentNullException.ThrowIfNull(serviceProvider);

        if (serviceProvider.GetService(typeof(OutboxSignals)) is null)
        {
            throw new InvalidOperationException("The outbox is not registered. Call AddEntityFrameworkOutbox<TContext>() on the Inngest builder first.");
        }

        return optionsBuilder.UseInngestOutbox();
    }
}
