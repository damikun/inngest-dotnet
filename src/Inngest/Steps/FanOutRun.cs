// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

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
    // A fail-fast run starts no unit after a failure and ends at once, without waiting for the units in flight: Inngest
    // cannot recall a step it has planned, and waiting would only delay the failure. A run that cannot go on throws: a
    // unit threw something other than a step failure, or the token was canceled while units were left.
    //
    // A replay starts the units in the order the run first did. The engine hands memoized results back in the order the
    // run recorded them, and each hand-back frees the lane that waited for it to start the next unit. The id of a unit
    // comes from its number alone, so that order decides only which units are found together, never which step is
    // which.
    //
    // That holds because the lanes run on the invocation's scheduler, where the engine sees them: it tells that the
    // function is idle when the scheduler has no work left, and a lane that went on somewhere else would start its next
    // unit while the engine decides what to report. So the lanes start there, whatever context the caller is in, and
    // wait with awaits that return there. A caller that is not on the scheduler, because it called after
    // ConfigureAwait(false) or from Task.Run, has the start queued with its execution context, as its steps would be. A
    // fake of the step tools has no scheduler, and the lanes then start where the caller is.
    public static Task<FanOutRun<T>> RunAsync(
        IStepTools step,
        int count,
        int width,
        bool failFast,
        Func<int, Task<T>> start,
        CancellationToken cancellationToken)
    {
        var lanes = new Lanes(count, Math.Min(width, count), failFast, start, cancellationToken);
        if (step is StepTools tools)
        {
            tools.RunOnScheduler(lanes.Start);
        }
        else
        {
            lanes.Start();
        }

        return lanes.Result;
    }

    private sealed class Lanes
    {
        private readonly object _gate = new();
        private readonly int _count;
        private readonly int _width;
        private readonly bool _failFast;
        private readonly Func<int, Task<T>> _start;
        private readonly CancellationToken _cancellationToken;
        private readonly T[] _values;
        private readonly StepFailedException?[] _failures;
        private readonly bool[] _settled;

        // The continuation of whoever awaits the result runs inline, right where the result is decided.
        private readonly TaskCompletionSource<FanOutRun<T>> _result = new();

        private int _active;
        private int _next;
        private StepFailedException? _first;

        public Lanes(int count, int width, bool failFast, Func<int, Task<T>> start, CancellationToken cancellationToken)
        {
            _count = count;
            _width = width;
            _active = width;
            _failFast = failFast;
            _start = start;
            _cancellationToken = cancellationToken;
            _values = new T[count];
            _failures = new StepFailedException?[count];
            _settled = new bool[count];
        }

        public Task<FanOutRun<T>> Result => _result.Task;

        public void Start()
        {
            if (_width == 0)
            {
                Decide();
                return;
            }

            for (var i = 0; i < _width; i++)
            {
                _ = LaneAsync();
            }
        }

        private async Task LaneAsync()
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
                    // ConfigureAwait(true) is for the analyzer, and says what is meant: the continuation returns to the
                    // scheduler. With false it would go to the thread pool, because the scheduler is not the default
                    // context, which is how a lane ends up off the scheduler.
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

            if (Interlocked.Decrement(ref _active) == 0)
            {
                Decide();
            }
        }

        // The number of the next unit, or -1 when this lane is done: there are none left, or the run has been decided.
        private int TakeNext()
        {
            Exception? canceled = null;
            var index = -1;
            lock (_gate)
            {
                if (!_result.Task.IsCompleted && !(_failFast && _first is not null) && _next < _count)
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

            if (_failFast)
            {
                Decide();
            }
        }

        // What the lanes have settled by now is the answer. Units in flight may settle later, and the answer does not
        // change with them. The result is set outside the lock, because the continuation of whoever waits runs right
        // there.
        private void Decide()
        {
            FanOutRun<T> snapshot;
            lock (_gate)
            {
                snapshot = new FanOutRun<T>([.. _values], [.. _failures], [.. _settled], _first);
            }

            _result.TrySetResult(snapshot);
        }

        private void Fail(Exception exception) => _result.TrySetException(exception);
    }
}
