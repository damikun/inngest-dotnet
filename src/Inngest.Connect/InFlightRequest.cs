// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using Inngest.Connect.Protocol;

namespace Inngest.Connect;

// An executor request the worker acknowledged and holds a lease for.
internal sealed class InFlightRequest
{
    private int _released;

    public InFlightRequest(GatewayExecutorRequestData data, GatewayConnection origin, TimeProvider time)
    {
        Data = data;
        Origin = origin;
        Lease = new RequestLease(data.LeaseId);

        // Ticks on the interval the gateway gave the connection the request came in on, from the acknowledgement on.
        LeaseTimer = new PeriodicTimer(origin.ExtendLeaseInterval, time);
    }

    public GatewayExecutorRequestData Data { get; }

    public GatewayConnection Origin { get; }

    public RequestLease Lease { get; }

    public PeriodicTimer LeaseTimer { get; }

    // Lets go of the lease, which stops the extensions. Returns true for the one call that did it.
    public bool Release()
    {
        if (Interlocked.Exchange(ref _released, 1) != 0)
        {
            return false;
        }

        Lease.Release();
        LeaseTimer.Dispose();
        return true;
    }
}

// The lease of an executor request. Every extension can hand out a new lease id, and the next extension must name the
// latest one. Each lease id is a lease of its own, so whoever read one can tell later, by reference, whether it is still
// the current one. That needs no second field that could change between two reads.
internal sealed class RequestLease
{
    private Lease? _current;

    // The lease the latest extension named, and before the first one the lease the request came with. An answer comes with
    // no lease id of its own, so it is taken to be about that one.
    private Lease _extended;

    public RequestLease(string leaseId)
    {
        _extended = new Lease(leaseId);
        _current = _extended;
    }

    // Null once the lease is given up or lost.
    public Lease? Current => Volatile.Read(ref _current);

    // Inngest sent the running request again with a new lease.
    public void Renew(string leaseId)
    {
        var renewed = new Lease(leaseId);
        var current = Volatile.Read(ref _current);
        while (current is not null)
        {
            var seen = Interlocked.CompareExchange(ref _current, renewed, current);
            if (ReferenceEquals(seen, current))
            {
                return;
            }

            current = seen;
        }
    }

    // Records the lease an extension names, before it goes out, so that an answer that comes back at once finds it.
    public void ExtensionSent(Lease lease) => Volatile.Write(ref _extended, lease);

    // The gateway extended the lease the latest extension named, under a new id. False when that lease was replaced
    // after the extension went out, such as by Inngest sending the running request again with a new lease.
    public bool TryExtend(string leaseId) => TryReplace(new Lease(leaseId));

    // The gateway did not extend the lease the latest extension named: it expired or another worker holds it now. False
    // when that lease was replaced after the extension went out, and the answer is about a lease no longer used.
    public bool TryLose() => TryReplace(null);

    public void Release() => Volatile.Write(ref _current, null);

    private bool TryReplace(Lease? next)
    {
        var extended = Volatile.Read(ref _extended);
        return ReferenceEquals(Interlocked.CompareExchange(ref _current, next, extended), extended);
    }
}

internal sealed class Lease(string id)
{
    public string Id { get; } = id;
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
