// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using System.Diagnostics.CodeAnalysis;
using System.Runtime.ExceptionServices;

namespace Inngest.Steps;

// What a run of numbered units produced, by unit number. A unit is a step, or a group of steps in a child run.
internal sealed class FanOutRun<T>
{
    private FanOutRun(T[] values, StepFailedException?[] failures, bool[] settled, StepFailedException? first)
    {
        Values = values;
        Failures = failures;
        Settled = settled;
        First = first;
    }

    // The result of each unit that finished without a failure.
    public T[] Values { get; }

    // The step failure of each unit that failed for good.
    public StepFailedException?[] Failures { get; }

    // Whether the unit finished, with a result or a failure. A fail-fast run leaves units unsettled.
    public bool[] Settled { get; }

    // The failure the lanes saw first, which is the one Inngest recorded first.
    public StepFailedException? First { get; }

    public void ThrowIfFailed()
    {
        if (First is not null)
        {
            ExceptionDispatchInfo.Capture(First).Throw();
        }
    }

    // Runs the units, at most `width` at a time, the way the workers of Parallel.ForEachAsync do: a lane starts a unit,
    // waits for it and takes the next one. A unit is a step, so a lane that waits is waiting for Inngest, and a window
    // of N units in flight is N lanes.
    //
    // A fail-fast run starts no unit after a failure and returns at once, without waiting for the units in flight:
    // Inngest cannot recall a step it has planned, and waiting would only delay the failure. A run that cannot go on
    // throws: a unit threw something other than a step failure, or the token was canceled while units were left.
    //
    // A replay starts the units in the order the run first did. The engine hands memoized results back in the order the
    // run recorded them, and each hand-back frees the lane that waited for it to start the next unit. The id of a unit
    // comes from its number alone, so that order decides only which units are found together, never which step is
    // which.
    public static async Task<FanOutRun<T>> RunAsync(
        int count,
        int width,
        bool failFast,
        Func<int, Task<T>> start,
        CancellationToken cancellationToken)
    {
        var lanes = new Lanes(count, failFast, start, cancellationToken);
        var running = new Task[Math.Min(width, count)];
        for (var i = 0; i < running.Length; i++)
        {
            running[i] = lanes.RunAsync();
        }

        // The awaits here, in the lanes and in the fan-out methods capture the context on purpose. The invocation's
        // scheduler is where steps are started and where the engine tells that the function is idle, and with
        // ConfigureAwait(false) a continuation that finds the scheduler as its context goes to the thread pool instead,
        // so a lane would start the next unit while the engine decides what to report.
        await Task.WhenAny(Task.WhenAll(running), lanes.Stopped).ConfigureAwait(true);
        return lanes.Finish();
    }

    private sealed class Lanes
    {
        private readonly object _gate = new();
        private readonly int _count;
        private readonly bool _failFast;
        private readonly Func<int, Task<T>> _start;
        private readonly CancellationToken _cancellationToken;
        private readonly T[] _values;
        private readonly StepFailedException?[] _failures;
        private readonly bool[] _settled;
        private readonly TaskCompletionSource _stopped = new();

        private int _next;
        private StepFailedException? _first;
        private Exception? _fatal;

        public Lanes(int count, bool failFast, Func<int, Task<T>> start, CancellationToken cancellationToken)
        {
            _count = count;
            _failFast = failFast;
            _start = start;
            _cancellationToken = cancellationToken;
            _values = new T[count];
            _failures = new StepFailedException?[count];
            _settled = new bool[count];
        }

        // Completes when the run must end without waiting for the lanes.
        public Task Stopped => _stopped.Task;

        // Lanes that are still waiting for a step may settle a unit later, so the caller gets a copy.
        public FanOutRun<T> Finish()
        {
            lock (_gate)
            {
                if (_fatal is not null)
                {
                    Rethrow(_fatal);
                }

                return new FanOutRun<T>([.. _values], [.. _failures], [.. _settled], _first);
            }
        }

        public async Task RunAsync()
        {
            while (TakeNext() is var index and >= 0)
            {
                Task<T> unit;
                try
                {
                    unit = _start(index);
                }
                catch (Exception exception)
                {
                    Fail(exception);
                    return;
                }

                try
                {
                    var value = await unit.ConfigureAwait(true);
                    lock (_gate)
                    {
                        _values[index] = value;
                        _settled[index] = true;
                    }
                }
                catch (StepFailedException failure)
                {
                    Settle(index, failure);
                }
                catch (Exception exception)
                {
                    // Not a unit that failed: a result that cannot be read, or a canceled call. Going on would only
                    // start units that fail the same way.
                    Fail(exception);
                }
            }
        }

        // The number of the next unit, or -1 when this lane is done: there are none left, or the run has failed.
        private int TakeNext()
        {
            Exception? canceled = null;
            var index = -1;
            lock (_gate)
            {
                if (_fatal is null && !(_failFast && _first is not null) && _next < _count)
                {
                    // Units are left, and starting them is what a canceled token forbids.
                    if (_cancellationToken.IsCancellationRequested)
                    {
                        canceled = new OperationCanceledException(_cancellationToken);
                    }
                    else
                    {
                        index = _next++;
                    }
                }
            }

            if (canceled is not null)
            {
                Fail(canceled);
            }

            return index;
        }

        private void Settle(int index, StepFailedException failure)
        {
            lock (_gate)
            {
                _failures[index] = failure;
                _settled[index] = true;
                _first ??= failure;
            }

            // Outside the lock, because the continuation of whoever waits may run right here.
            if (_failFast)
            {
                _stopped.TrySetResult();
            }
        }

        private void Fail(Exception exception)
        {
            lock (_gate)
            {
                _fatal ??= exception;
            }

            _stopped.TrySetResult();
        }

        [DoesNotReturn]
        private static void Rethrow(Exception exception)
        {
            ExceptionDispatchInfo.Capture(exception).Throw();
            throw exception;
        }
    }
}
