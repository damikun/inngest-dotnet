// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.

using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Inngest.EntityFrameworkCore;

// Wakes the dispatcher once events are in the table for good: after a save that used its own transaction, and after the
// commit when the code opened one around the save. A wake-up is a hint and the poll finds the events without it, so the
// cases this cannot see, such as a TransactionScope that commits later, only cost the time until the next poll.
//
// One instance serves every context of the app, and it needs nothing from the code that configured the context, which
// may not have a service provider at hand (the AddDbContext overloads without one, OnConfiguring). What it knows about a
// save, it knows from the context itself: AddInngestEvent marks the context, and the marks are cleared as soon as they
// were acted on or the transaction ended. The dispatcher's signal it finds in the application services of the context.
internal sealed class OutboxInterceptor : SaveChangesInterceptor, IDbTransactionInterceptor
{
    private OutboxInterceptor()
    {
    }

    public static OutboxInterceptor Instance { get; } = new();

    public override int SavedChanges(SaveChangesCompletedEventData eventData, int result)
    {
        Saved(eventData.Context);
        return result;
    }

    public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
    {
        Saved(eventData.Context);
        return ValueTask.FromResult(result);
    }

    public void TransactionCommitted(DbTransaction transaction, TransactionEndEventData eventData) => Committed(eventData.Context);

    public Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        Committed(eventData.Context);
        return Task.CompletedTask;
    }

    public void TransactionRolledBack(DbTransaction transaction, TransactionEndEventData eventData) => RolledBack(eventData.Context);

    public Task TransactionRolledBackAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        RolledBack(eventData.Context);
        return Task.CompletedTask;
    }

    private void Saved(DbContext? context)
    {
        if (!OutboxPending.TryGet(context, out var pending) || !pending.Added)
        {
            return;
        }

        // SaveChanges commits a transaction of its own unless one is open, so without one the events are visible now.
        if (context!.Database.CurrentTransaction is null)
        {
            Wake(context, pending);
            return;
        }

        pending.Added = false;
        pending.AwaitingCommit = true;
    }

    private void Committed(DbContext? context)
    {
        if (OutboxPending.TryGet(context, out var pending) && pending.AwaitingCommit)
        {
            Wake(context!, pending);
        }
    }

    // The events saved in the transaction went with it. Events added since the last save are still waiting for one.
    private static void RolledBack(DbContext? context)
    {
        if (OutboxPending.TryGet(context, out var pending))
        {
            pending.AwaitingCommit = false;
        }
    }

    private static void Wake(DbContext context, OutboxPending pending)
    {
        pending.Added = false;
        pending.AwaitingCommit = false;

        // Without the outbox registered nobody listens, and nothing was added with AddInngestEvent in the first place.
        if (OutboxServices.ApplicationServicesOf(context)?.GetService(typeof(OutboxSignals)) is OutboxSignals signals)
        {
            signals.For(context.GetType()).Notify();
        }
    }
}
