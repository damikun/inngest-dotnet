// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

namespace Inngest.Connect;

// The state the worker publishes. A state and the signal that ends it are swapped in with one compare-and-swap, so
// readers never lock: a waiter either sees the new state or holds the signal that the change completes.
internal sealed class InngestConnection : IInngestConnection
{
    private Snapshot _current = new(InngestConnectionState.Connecting);

    public InngestConnectionState State => Volatile.Read(ref _current).State;

    public async Task<InngestConnectionState> WaitForStateChangedAsync(InngestConnectionState lastObservedState, CancellationToken cancellationToken = default)
    {
        while (true)
        {
            var current = Volatile.Read(ref _current);
            if (current.State != lastObservedState)
            {
                return current.State;
            }

            await current.Replaced.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    // Moves are ignored where the state machine has no such transition, so the worker can report what happened without
    // checking where it stands. As in the TypeScript SDK, a closing worker stays closing while it reconnects to finish
    // its executions.
    public void MoveTo(InngestConnectionState state)
    {
        while (true)
        {
            var current = Volatile.Read(ref _current);
            if (!CanMove(current.State, state))
            {
                return;
            }

            if (Interlocked.CompareExchange(ref _current, new Snapshot(state), current) == current)
            {
                current.Replaced.TrySetResult();
                return;
            }
        }
    }

    private static bool CanMove(InngestConnectionState from, InngestConnectionState to) => to switch
    {
        InngestConnectionState.Active => from is InngestConnectionState.Connecting or InngestConnectionState.Reconnecting,
        InngestConnectionState.Reconnecting => from is InngestConnectionState.Active,
        InngestConnectionState.Closing => from is not (InngestConnectionState.Closing or InngestConnectionState.Closed),
        InngestConnectionState.Closed => from is not InngestConnectionState.Closed,
        _ => false,
    };

    private sealed class Snapshot(InngestConnectionState state)
    {
        public InngestConnectionState State { get; } = state;

        // Waiters resume on the thread pool, never on the worker's loop that made the change.
        public TaskCompletionSource Replaced { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
