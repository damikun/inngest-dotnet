// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using Inngest.Rest;

namespace Inngest;

/// <summary>
/// Function runs through the Inngest REST API, authenticated with the app's signing key. Inngest caches these
/// answers for a few seconds (a run for 3, the runs of an event for 15), so a run can look a little behind; one that
/// was just started may not be listed yet. Failures throw InngestException with Inngest's message.
/// </summary>
public interface IInngestRunsClient
{
    /// <summary>Gets a run with its status and, once it has ended, its output.</summary>
    Task<FunctionRun> GetRunAsync(string runId, CancellationToken cancellationToken = default);

    /// <summary>Lists the runs an event started, by the event id that sending it returned.</summary>
    Task<IReadOnlyList<FunctionRun>> GetEventRunsAsync(string eventId, CancellationToken cancellationToken = default);

    /// <summary>Cancels a run: a step that is running finishes, and no further steps run.</summary>
    Task CancelRunAsync(string runId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads the run until it has ended, completed, failed or cancelled, and returns it with its output. Reads start a
    /// quarter of a second apart and grow to 2 s apart. A run that is not readable yet (404), a refused read (429), a
    /// server error and no connection count as not ended yet; any other error is thrown.
    /// </summary>
    /// <param name="runId">The run, such as one that <see cref="GetEventRunsAsync"/> listed.</param>
    /// <param name="timeout">How long to wait, or <see cref="Timeout.InfiniteTimeSpan"/> to wait until cancelled.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <exception cref="TimeoutException">The run did not end in time. The message says what was last seen.</exception>
    Task<FunctionRun> WaitForRunAsync(string runId, TimeSpan timeout, CancellationToken cancellationToken = default) =>
        RunWaiter.For(this).WaitForRunAsync(runId, timeout, cancellationToken);

    /// <summary>
    /// Waits until the event has started at least <paramref name="expectedRuns"/> runs and every run it started has
    /// ended, and returns them all. An event starts one run for each function it triggers, once Inngest has processed
    /// it; flow control such as a debounce or a batch can delay or merge runs. Inngest caches the runs of an event for
    /// 15 s, so a run that starts after the first read can take that long to show up. Runs that are listed are read one
    /// by one until they end.
    /// </summary>
    /// <param name="eventId">The id that sending the event returned.</param>
    /// <param name="expectedRuns">How many runs the event starts, at least 1. For an event that may start none, use
    /// <see cref="WaitForEventRunsToSettleAsync"/>.</param>
    /// <param name="timeout">How long to wait, or <see cref="Timeout.InfiniteTimeSpan"/> to wait until cancelled.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <exception cref="TimeoutException">Fewer runs started, or not all of them ended, in time.</exception>
    Task<IReadOnlyList<FunctionRun>> WaitForEventRunsAsync(string eventId, int expectedRuns, TimeSpan timeout, CancellationToken cancellationToken = default) =>
        RunWaiter.For(this).WaitForEventRunsAsync(eventId, expectedRuns, timeout, cancellationToken);

    /// <summary>
    /// Waits until the runs the event started have all ended and no other run has shown up for
    /// <paramref name="settleTime"/>, and returns them, for when the number of runs is not known: an empty list once an
    /// event that started no run has settled. Time is measured between Inngest's own reads of the list, which it caches
    /// for 15 s, so a settled list was read twice at least <paramref name="settleTime"/> apart.
    /// </summary>
    /// <param name="eventId">The id that sending the event returned.</param>
    /// <param name="settleTime">How long the list must stay the same. Runs start within a second or so of the event,
    /// unless flow control, such as a debounce, holds them back.</param>
    /// <param name="timeout">How long to wait, or <see cref="Timeout.InfiniteTimeSpan"/> to wait until cancelled.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <exception cref="TimeoutException">The runs did not end, or the list kept changing, until the timeout.</exception>
    Task<IReadOnlyList<FunctionRun>> WaitForEventRunsToSettleAsync(string eventId, TimeSpan settleTime, TimeSpan timeout, CancellationToken cancellationToken = default) =>
        RunWaiter.For(this).WaitForEventRunsToSettleAsync(eventId, settleTime, timeout, cancellationToken);
}
