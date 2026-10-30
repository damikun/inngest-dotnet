// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.

using Inngest;
using Inngest.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace Microsoft.EntityFrameworkCore;

public static class InngestOutboxDbContextExtensions
{
    /// <summary>
    /// Adds the data as an event named T.EventName to the unit of work of the context. Nothing is sent now: the event is
    /// saved by the next SaveChanges together with the other changes, committed or rolled back with them, and a
    /// dispatcher sends it after the commit. Returns the row, whose Id is the id Inngest gets the event under. The id is
    /// generated unless one is given; an id the code derives from the work, such as an order number, makes a second
    /// attempt at the same work fail at the unique key instead of sending a second event.
    /// </summary>
    public static InngestOutboxMessage AddInngestEvent<T>(this DbContext context, T data, string? id = null)
        where T : IInngestEventData
    {
        ArgumentNullException.ThrowIfNull(data);
        return context.AddInngestEvent(new InngestEvent(T.EventName, data) { Version = T.EventVersion, Id = id });
    }

    /// <summary>
    /// Adds an event, named by a string, to the unit of work of the context; see the typed overload. The data and the
    /// user data are serialized now, with the SDK's serializer options, so an event that cannot be sent fails here and
    /// not in the dispatcher. Id, Timestamp, Version and the sessions of Meta are kept; a Timestamp left out is the time
    /// of this call.
    /// </summary>
    public static InngestOutboxMessage AddInngestEvent(this DbContext context, InngestEvent @event)
    {
        ArgumentNullException.ThrowIfNull(context);

        var message = WriterOf(context).Create(@event);
        context.Set<InngestOutboxMessage>().Add(message);
        OutboxPending.Of(context).Added = true;
        return message;
    }

    private static OutboxEventWriter WriterOf(DbContext context)
    {
        var name = context.GetType().Name;
        if (context.Model.FindEntityType(typeof(InngestOutboxMessage)) is null)
        {
            throw new InvalidOperationException($"{name} does not map the outbox table. Call modelBuilder.AddInngestOutbox() in OnModelCreating.");
        }

        // The application's services are where AddDbContext leaves them, and the writer is one of them.
        return context.GetService<IDbContextOptions>().FindExtension<CoreOptionsExtension>()?.ApplicationServiceProvider?.GetService(typeof(OutboxEventWriter)) as OutboxEventWriter
            ?? throw new InvalidOperationException(
                $"{name} cannot reach the outbox. Call AddEntityFrameworkOutbox<{name}>() on the Inngest builder, and register the context with AddDbContext, so that it knows the application's services.");
    }
}
