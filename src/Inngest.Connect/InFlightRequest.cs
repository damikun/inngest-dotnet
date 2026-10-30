// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using Inngest.Connect.Protocol;

namespace Inngest.Connect;

// An executor request the worker acknowledged and holds a lease for. Every extension can hand out a new lease id,
// and the next extension must name the latest one.
internal sealed class InFlightRequest
{
    private string? _leaseId;
    private int _released;

    // How often the lease was renewed, and how often it had been when the latest extension went out. An answer comes
    // with no lease id of its own, so this is how a late answer about an older lease is told apart.
    private int _renewals;
    private int _renewalsAtExtension;

    public InFlightRequest(GatewayExecutorRequestData data, GatewayConnection origin, TimeProvider time)
    {
        Data = data;
        Origin = origin;
        _leaseId = data.LeaseId;

        // Ticks on the interval the gateway gave the connection the request came in on, from the acknowledgement on.
        LeaseTimer = new PeriodicTimer(origin.ExtendLeaseInterval, time);
    }

    public GatewayExecutorRequestData Data { get; }

    public GatewayConnection Origin { get; }

    public PeriodicTimer LeaseTimer { get; }

    // Null once the lease is given up or lost.
    public string? LeaseId => Volatile.Read(ref _leaseId);

    public int Renewals => Volatile.Read(ref _renewals);

    // An answer to the latest extension is about the current lease only if nothing renewed it after the extension
    // went out, such as Inngest sending the running request again with a new lease.
    public bool AnswerIsCurrent => Volatile.Read(ref _renewalsAtExtension) == Volatile.Read(ref _renewals);

    public void RenewLease(string leaseId)
    {
        var current = Volatile.Read(ref _leaseId);
        while (current is not null)
        {
            var seen = Interlocked.CompareExchange(ref _leaseId, leaseId, current);
            if (ReferenceEquals(seen, current))
            {
                Interlocked.Increment(ref _renewals);
                return;
            }

            current = seen;
        }
    }

    // Records the renewals seen before the lease id of an extension was read.
    public void ExtensionSent(int renewals) => Volatile.Write(ref _renewalsAtExtension, renewals);

    // Lets go of the lease, which stops the extensions. Returns true for the one call that did it.
    public bool Release()
    {
        if (Interlocked.Exchange(ref _released, 1) != 0)
        {
            return false;
        }

        Volatile.Write(ref _leaseId, null);
        LeaseTimer.Dispose();
        return true;
    }
}

// Counts the executions that hold a lease. Once shutdown has begun it admits no more, so that the worker knows
// exactly when the last one is done and it may disconnect.
internal sealed class InFlightGate
{
    private const int ShuttingDownFlag = 1 << 30;

    private int _state;

    public int Count => Volatile.Read(ref _state) & ~ShuttingDownFlag;

    public bool IsShuttingDown => (Volatile.Read(ref _state) & ShuttingDownFlag) != 0;

    public bool TryEnter()
    {
        var state = Volatile.Read(ref _state);
        while ((state & ShuttingDownFlag) == 0)
        {
            var seen = Interlocked.CompareExchange(ref _state, state + 1, state);
            if (seen == state)
            {
                return true;
            }

            state = seen;
        }

        return false;
    }

    // Returns true when this was the last execution and shutdown has begun.
    public bool Exit() => Interlocked.Decrement(ref _state) == ShuttingDownFlag;

    // Returns true when nothing is in flight.
    public bool BeginShutdown() => (Interlocked.Or(ref _state, ShuttingDownFlag) & ~ShuttingDownFlag) == 0;
}
