// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using System.Diagnostics;
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

    // When the user code running on this context now started, as a Stopwatch timestamp, or 0 while none runs. Another
    // thread reads it to tell when that code holds the thread.
    private long _runningSince;

    // The items in the queue, counted apart from it for the watchdog on another thread, since only the engine's thread may
    // read a single-reader channel.
    private int _queued;

    public bool IsCurrent => Current == this;

    // Once the invocation has answered, work goes to the thread pool, as it would without this context. It may complete
    // a task that outlives the call, such as one a singleton caches, or release a semaphore; dropped, it would leave
    // everything that waits on those waiting forever.
    public override void Post(SendOrPostCallback d, object? state)
    {
        // Counted first, so that the count is never below what the queue holds.
        Interlocked.Increment(ref _queued);
        if (!_queue.Writer.TryWrite(new WorkItem(d, state)))
        {
            Interlocked.Decrement(ref _queued);
            RunLater(new WorkItem(d, state));
        }
    }

    // Blocking the user code until a callback runs would deadlock the single logical thread.
    public override void Send(SendOrPostCallback d, object? state) =>
        throw new NotSupportedException("Synchronous calls into an Inngest function invocation are not supported.");

    public override SynchronizationContext CreateCopy() => this;

    // Starts user code with this context installed so that its awaits resume here.
    public T Run<T>(Func<T> start)
    {
        var previous = Current;
        SetSynchronizationContext(this);
        Volatile.Write(ref _runningSince, Stopwatch.GetTimestamp());
        try
        {
            return start();
        }
        finally
        {
            Volatile.Write(ref _runningSince, 0);
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
                Interlocked.Decrement(ref _queued);
                count++;
                Volatile.Write(ref _runningSince, Stopwatch.GetTimestamp());
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
            Volatile.Write(ref _runningSince, 0);
            SetSynchronizationContext(previous);
        }

        _unhandled?.Throw();
        return count;
    }

    // Whether code on this context has run for at least the given time while work waits for it. Nothing else runs that
    // work, so the code is most likely blocked on a task that needs it, which then never completes. Called from another
    // thread, so it reads only the count.
    public bool IsHeldFor(TimeSpan time)
    {
        var since = Volatile.Read(ref _runningSince);
        return since != 0 && Stopwatch.GetElapsedTime(since) >= time && Volatile.Read(ref _queued) > 0;
    }

    // Completes when more work is queued, for example when awaited I/O finishes.
    public ValueTask<bool> WaitForWorkAsync(CancellationToken cancellationToken) =>
        _queue.Reader.WaitToReadAsync(cancellationToken);

    // Stops taking work once the invocation has responded. Work still queued, such as I/O that finished as the call
    // answered, and work posted later run on the thread pool. Steps the abandoned function calls still never complete.
    public void Close()
    {
        _queue.Writer.TryComplete();
        while (_queue.Reader.TryRead(out var item))
        {
            Interlocked.Decrement(ref _queued);
            RunLater(item);
        }
    }

    // Nothing can observe a failure of late work any more, and on the thread pool it would end the process. Only async
    // void code throws here, so the exception is dropped.
    private static void RunLater(WorkItem item) =>
        ThreadPool.QueueUserWorkItem(
            static item =>
            {
                try
                {
                    item.Callback(item.State);
                }
                catch (Exception)
                {
                    // Dropped, see above.
                }
            },
            item,
            preferLocal: false);

    private readonly record struct WorkItem(SendOrPostCallback Callback, object? State);
}
