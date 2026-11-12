// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using System.Globalization;

namespace Inngest.Rest;

// The runs of an event as one read of the REST API found them. FetchedAt is when Inngest read them, which a cached
// answer repeats; null when the reader does not know.
internal readonly record struct EventRunsRead(IReadOnlyList<FunctionRun> Runs, DateTimeOffset? FetchedAt);

// How often a wait reads the REST API: the first delay, which grows by half each time up to the largest.
internal sealed record RunPolling(TimeSpan FirstDelay, TimeSpan MaxDelay)
{
    // Inngest caches a run for 3 s and the runs of an event for 15 s, so reading more often than every 2 s mostly
    // returns the same answer.
    public static readonly RunPolling Default = new(TimeSpan.FromMilliseconds(250), TimeSpan.FromSeconds(2));
}

// Waits for runs to end by reading the REST API until they have. A run has ended once it has an end time: the runs of
// an event can read Completed while a sleep of the run is still pending. Errors that can pass (no connection, a 404
// for a run that is not readable yet, a 429 or a 5xx) count as "not yet"; any other error ends the wait.
internal sealed class RunWaiter(
    Func<string, CancellationToken, Task<FunctionRun>> getRun,
    Func<string, CancellationToken, Task<EventRunsRead>> getEventRuns,
    TimeProvider time,
    RunPolling polling)
{
    private TimeProvider Time => time;

    private RunPolling Polling => polling;

    public static RunWaiter For(IInngestRunsClient runs, TimeProvider? time = null) => new(
        runs.GetRunAsync,
        async (eventId, cancellationToken) => new EventRunsRead(await runs.GetEventRunsAsync(eventId, cancellationToken).ConfigureAwait(false), null),
        time ?? TimeProvider.System,
        RunPolling.Default);

    public async Task<FunctionRun> WaitForRunAsync(string runId, TimeSpan timeout, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(runId);
        CheckTimeout(timeout);

        var wait = new Wait(this, timeout, cancellationToken);
        try
        {
            FunctionRun? last = null;
            while (true)
            {
                last = await wait.TryAsync(token => getRun(runId, token)).ConfigureAwait(false) ?? last;
                if (last is { EndedAt: not null })
                {
                    return last;
                }

                await wait.DelayAsync().ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (wait.TimedOut && exception is OperationCanceledException or InngestException)
        {
            throw wait.Timeout(
                $"Run {runId} did not end within {Describe(timeout)}. Last seen: {(wait.LastRun is null ? "nothing" : Describe(wait.LastRun))}.",
                exception);
        }
        finally
        {
            wait.Dispose();
        }
    }

    // Done once at least expectedRuns runs are listed and every listed run has ended.
    public Task<IReadOnlyList<FunctionRun>> WaitForEventRunsAsync(string eventId, int expectedRuns, TimeSpan timeout, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(eventId);
        ArgumentOutOfRangeException.ThrowIfLessThan(expectedRuns, 1);
        CheckTimeout(timeout);

        return WaitForEventRunsAsync(
            eventId,
            timeout,
            (runs, _) => runs.Count >= expectedRuns && runs.All(static run => run.EndedAt is not null),
            $"start {expectedRuns} run(s) that end",
            cancellationToken);
    }

    // Done once every listed run has ended and the list has stayed the same for settleTime, as Inngest read it: a
    // cached answer repeats the time of the read it repeats, so the list only counts as unchanged once a later read
    // confirms it.
    public Task<IReadOnlyList<FunctionRun>> WaitForEventRunsToSettleAsync(string eventId, TimeSpan settleTime, TimeSpan timeout, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(eventId);
        ArgumentOutOfRangeException.ThrowIfLessThan(settleTime, TimeSpan.Zero);
        CheckTimeout(timeout);

        return WaitForEventRunsAsync(
            eventId,
            timeout,
            (runs, unchangedFor) => unchangedFor >= settleTime && runs.All(static run => run.EndedAt is not null),
            $"settle for {Describe(settleTime)} with every run ended",
            cancellationToken);
    }

    private async Task<IReadOnlyList<FunctionRun>> WaitForEventRunsAsync(
        string eventId,
        TimeSpan timeout,
        Func<IReadOnlyList<FunctionRun>, TimeSpan, bool> done,
        string what,
        CancellationToken cancellationToken)
    {
        var wait = new Wait(this, timeout, cancellationToken);

        // The latest of each run, in the order the list first named them. A run read on its own is fresher than the
        // list, which Inngest caches for longer, so an ended run stays ended.
        var runs = new List<FunctionRun>();
        DateTimeOffset? changedAt = null;
        var unchangedFor = TimeSpan.Zero;
        try
        {
            while (true)
            {
                if (await wait.TryAsync(token => getEventRuns(eventId, token)).ConfigureAwait(false) is { } read)
                {
                    var fetchedAt = read.FetchedAt ?? time.GetUtcNow();
                    if (Merge(runs, read.Runs) || changedAt is null)
                    {
                        changedAt = fetchedAt;
                    }

                    unchangedFor = fetchedAt - changedAt.Value;
                }

                // A run is read on its own until it has ended, which Inngest caches for less time than the list.
                for (var i = 0; i < runs.Count; i++)
                {
                    if (runs[i].EndedAt is null && await wait.TryAsync(token => getRun(runs[i].RunId, token)).ConfigureAwait(false) is { } run)
                    {
                        runs[i] = run;
                    }
                }

                if (changedAt is not null && done(runs, unchangedFor))
                {
                    return [.. runs];
                }

                await wait.DelayAsync().ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (wait.TimedOut && exception is OperationCanceledException or InngestException)
        {
            var seen = changedAt is null ? "nothing" : runs.Count == 0 ? "no runs" : string.Join(", ", runs.Select(Describe));
            throw wait.Timeout($"Event {eventId} did not {what} within {Describe(timeout)}. Last seen: {seen}.", exception);
        }
        finally
        {
            wait.Dispose();
        }
    }

    // Adds the runs that are new and returns whether there were any; updates those that have not ended yet.
    private static bool Merge(List<FunctionRun> runs, IReadOnlyList<FunctionRun> listed)
    {
        var added = false;
        foreach (var run in listed)
        {
            var index = runs.FindIndex(known => known.RunId == run.RunId);
            if (index < 0)
            {
                runs.Add(run);
                added = true;
            }
            else if (runs[index].EndedAt is null)
            {
                runs[index] = run;
            }
        }

        return added;
    }

    private static void CheckTimeout(TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero && timeout != System.Threading.Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), timeout, "The timeout must be positive, or Timeout.InfiniteTimeSpan.");
        }
    }

    private static string Describe(FunctionRun run) =>
        $"run {run.RunId} {run.Status}{(run.EndedAt is null ? string.Empty : " (ended)")}";

    private static string Describe(TimeSpan span) =>
        span == System.Threading.Timeout.InfiniteTimeSpan ? "no limit" : $"{span.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture)} s";

    private static bool CanPass(InngestException exception) =>
        exception.StatusCode is null or 404 or 429 or >= 500;

    // One wait: its deadline, its delays and the last error that it let pass.
    private sealed class Wait : IDisposable
    {
        private readonly RunWaiter _waiter;
        private readonly CancellationToken _cancellationToken;
        private readonly CancellationTokenSource? _deadline;
        private readonly CancellationTokenSource? _linked;
        private TimeSpan _nextDelay;
        private InngestException? _lastError;

        public Wait(RunWaiter waiter, TimeSpan timeout, CancellationToken cancellationToken)
        {
            _waiter = waiter;
            _cancellationToken = cancellationToken;
            _nextDelay = waiter.Polling.FirstDelay;
            if (timeout != System.Threading.Timeout.InfiniteTimeSpan)
            {
                _deadline = new CancellationTokenSource(timeout, waiter.Time);
                _linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _deadline.Token);
            }
        }

        public bool TimedOut => _deadline?.IsCancellationRequested == true && !_cancellationToken.IsCancellationRequested;

        public FunctionRun? LastRun { get; private set; }

        private CancellationToken Token => _linked?.Token ?? _cancellationToken;

        // The result of read, or null when it failed in a way that can pass.
        public async Task<T?> TryAsync<T>(Func<CancellationToken, Task<T>> read)
            where T : struct
        {
            try
            {
                return await read(Token).ConfigureAwait(false);
            }
            catch (InngestException exception) when (CanPass(exception) && !Token.IsCancellationRequested)
            {
                _lastError = exception;
                return null;
            }
        }

        public async Task<FunctionRun?> TryAsync(Func<CancellationToken, Task<FunctionRun>> read)
        {
            try
            {
                var run = await read(Token).ConfigureAwait(false);
                LastRun = run;
                return run;
            }
            catch (InngestException exception) when (CanPass(exception) && !Token.IsCancellationRequested)
            {
                _lastError = exception;
                return null;
            }
        }

        public async Task DelayAsync()
        {
            await Task.Delay(_nextDelay, _waiter.Time, Token).ConfigureAwait(false);
            _nextDelay = TimeSpan.FromTicks(Math.Min(_nextDelay.Ticks * 3 / 2, _waiter.Polling.MaxDelay.Ticks));
        }

        // A read that the deadline cut short ends in a cancellation, or in an InngestException that says it timed out.
        public TimeoutException Timeout(string message, Exception cancellation) =>
            new(_lastError is null ? message : $"{message} Last error: {_lastError.Message}", _lastError ?? cancellation);

        public void Dispose()
        {
            _linked?.Dispose();
            _deadline?.Dispose();
        }
    }
}
