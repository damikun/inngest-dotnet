// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

namespace Inngest;

/// <summary>
/// Manages functions, runs and events through the Inngest v2 REST API, authenticated with the app's signing key and
/// sent to the app's environment. Inngest marks these endpoints Beta. They came with Inngest v1.50 to v1.52, and the dev
/// server and self-hosted server do not have all of them (see each member). Failures throw InngestException with
/// Inngest's message and code, such as <c>not_found</c>. Apps and functions are named by the ids the app registered
/// them with, the function's without the app id in front.
/// </summary>
public interface IInngestManagementClient
{
    /// <summary>
    /// Pauses a function: its new runs wait until it is resumed (dev server) or are skipped (Inngest Cloud, where the
    /// change takes up to a minute). Pausing a paused function changes nothing.
    /// </summary>
    Task<FunctionInfo> PauseFunctionAsync(string appId, string functionId, CancellationToken cancellationToken = default);

    /// <summary>Resumes a paused function. Resuming a function that is not paused changes nothing.</summary>
    Task<FunctionInfo> ResumeFunctionAsync(string appId, string functionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Pauses up to 500 functions of an app in one request, and returns an outcome for each, in the order given: a
    /// function that is not there fails on its own without failing the others.
    /// </summary>
    Task<IReadOnlyList<FunctionPauseResult>> PauseFunctionsAsync(string appId, IReadOnlyList<string> functionIds, CancellationToken cancellationToken = default);

    /// <summary>Resumes up to 500 functions of an app in one request, as <see cref="PauseFunctionsAsync"/> pauses them.</summary>
    Task<IReadOnlyList<FunctionPauseResult>> ResumeFunctionsAsync(string appId, IReadOnlyList<string> functionIds, CancellationToken cancellationToken = default);

    /// <summary>
    /// Cancels the runs of a function that were queued in a time range, and that match the expression if one is given.
    /// Inngest cancels them in the background: runs that have not started never start, running ones stop before their
    /// next step, and sleeping or waiting ones when they would resume. Each call creates another cancellation.
    /// </summary>
    Task<RunCancellation> CancelRunsAsync(string appId, string functionId, CancelRunsRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists the events Inngest received, newest first, a page at a time as the enumeration goes on. Without a time
    /// range, those of the last hour; every page uses the range of the first, so events that arrive meanwhile do not
    /// shift the pages.
    /// </summary>
    IAsyncEnumerable<EventInfo> ListEventsAsync(EventQuery? query = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets an event by the id that sending it returned, with its payload when asked for. Inngest stores an event a
    /// moment after it accepts it, and answers 404 (<c>not_found</c>) until then.
    /// </summary>
    Task<EventInfo> GetEventAsync(string eventId, bool includePayload = false, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists the syncs of an app, newest first, a page at a time as the enumeration goes on. The dev server and the
    /// self-hosted server answer 501 (not implemented) to this.
    /// </summary>
    IAsyncEnumerable<AppSync> ListAppSyncsAsync(string appId, int? pageSize = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists runs, newest first unless the query says otherwise, a page at a time as the enumeration goes on. The dev
    /// server and the self-hosted server answer 501 (not implemented) to a filter on the Paused status.
    /// </summary>
    IAsyncEnumerable<RunSummary> ListRunsAsync(RunQuery? query = null, CancellationToken cancellationToken = default);
}
