// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.

using System.Collections.Concurrent;

namespace Inngest.EntityFrameworkCore;

// One signal per DbContext type, so that a save through one context wakes the dispatcher of that context and not the
// others. The interceptor is shared by all contexts and finds the signal by the type of the context that saved.
internal sealed class OutboxSignals
{
    private readonly ConcurrentDictionary<Type, OutboxSignal> _signals = new();

    public OutboxSignal For(Type contextType) => _signals.GetOrAdd(contextType, static _ => new OutboxSignal());
}
