// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using System.Runtime.ExceptionServices;
using System.Threading.Channels;

namespace Inngest.Execution;

// Runs the user code of one invocation on a single logical thread, the way a JavaScript event loop would. Every
// continuation that resumes user code is posted here, so once RunPending returns with nothing queued and the
// function has not finished, the function is waiting on something: a step that will not complete in this
// invocation, or real I/O whose continuation will be posted later. That is the signal the engine uses to decide
// when to report steps, without timers or guessing.
internal sealed class InvocationScheduler : SynchronizationContext
{
    private readonly Channel<WorkItem> _queue = Channel.CreateUnbounded<WorkItem>(
        new UnboundedChannelOptions { SingleReader = true, AllowSynchronousContinuations = false });

    private ExceptionDispatchInfo? _unhandled;

    public bool IsCurrent => Current == this;

    public override void Post(SendOrPostCallback d, object? state) => _queue.Writer.TryWrite(new WorkItem(d, state));

    // Blocking the user code until a callback runs would deadlock the single logical thread.
    public override void Send(SendOrPostCallback d, object? state) =>
        throw new NotSupportedException("Synchronous calls into an Inngest function invocation are not supported.");

    public override SynchronizationContext CreateCopy() => this;

    // Starts user code with this context installed so that its awaits resume here.
    public T Run<T>(Func<T> start)
    {
        var previous = Current;
        SetSynchronizationContext(this);
        try
        {
            return start();
        }
        finally
        {
            SetSynchronizationContext(previous);
        }
    }

    // Runs queued continuations until none are left, or until limit have run so that code that keeps yielding
    // cannot hold up the engine forever. Returns how many ran.
    public int RunPending(int limit = int.MaxValue)
    {
        var previous = Current;
        SetSynchronizationContext(this);
        var count = 0;
        try
        {
            while (count < limit && _queue.Reader.TryRead(out var item))
            {
                count++;
                try
                {
                    item.Callback(item.State);
                }
                catch (Exception exception)
                {
                    // Only async void code can throw here; async Task code stores exceptions in its task.
                    _unhandled ??= ExceptionDispatchInfo.Capture(exception);
                }
            }
        }
        finally
        {
            SetSynchronizationContext(previous);
        }

        _unhandled?.Throw();
        return count;
    }

    // Completes when more work is queued, for example when awaited I/O finishes.
    public ValueTask<bool> WaitForWorkAsync(CancellationToken cancellationToken) =>
        _queue.Reader.WaitToReadAsync(cancellationToken);

    // Stops accepting work once the invocation has responded; late continuations are dropped.
    public void Close() => _queue.Writer.TryComplete();

    private readonly record struct WorkItem(SendOrPostCallback Callback, object? State);
}
