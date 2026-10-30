// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.

using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;

namespace Inngest.EntityFrameworkCore;

// What the interceptor needs to know about the events a context added, kept for the life of the context instance. A
// context is used by one flow of execution at a time, so the flags need no locks.
internal sealed class OutboxPending
{
    private static readonly ConditionalWeakTable<DbContext, OutboxPending> Contexts = new();

    // Events were added and the save that stores them has not succeeded yet.
    public bool Added { get; set; }

    // The save succeeded inside a transaction that has not ended; its commit is the moment the dispatcher can see the events.
    public bool AwaitingCommit { get; set; }

    public static OutboxPending Of(DbContext context) => Contexts.GetValue(context, static _ => new OutboxPending());

    public static bool TryGet(DbContext? context, [NotNullWhen(true)] out OutboxPending? pending)
    {
        pending = null;
        return context is not null && Contexts.TryGetValue(context, out pending);
    }
}
