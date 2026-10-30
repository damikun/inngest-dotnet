// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.

using Inngest.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Microsoft.EntityFrameworkCore;

public static class InngestOutboxDbContextOptionsBuilderExtensions
{
    /// <summary>
    /// Lets saves wake the dispatcher: the dispatcher looks at the table as soon as a save that added events has
    /// committed, instead of at the next poll. Without it events are still sent, up to one poll interval later. Use it
    /// where the context is registered, with the provider of the service container, for example
    /// AddDbContext&lt;ShopContext&gt;((services, options) =&gt; options.UseNpgsql(connectionString).UseInngestOutbox(services)).
    /// It needs AddEntityFrameworkOutbox to have been called on the Inngest builder. When the save is inside a
    /// transaction of the code's own, the wake-up comes with its commit; with a TransactionScope it comes too early, and
    /// the poll finds the events.
    /// </summary>
    public static DbContextOptionsBuilder UseInngestOutbox(this DbContextOptionsBuilder optionsBuilder, IServiceProvider serviceProvider)
    {
        ArgumentNullException.ThrowIfNull(optionsBuilder);
        ArgumentNullException.ThrowIfNull(serviceProvider);

        var interceptor = serviceProvider.GetService<OutboxInterceptor>()
            ?? throw new InvalidOperationException("The outbox is not registered. Call AddEntityFrameworkOutbox<TContext>() on the Inngest builder first.");
        return optionsBuilder.AddInterceptors(interceptor);
    }
}
