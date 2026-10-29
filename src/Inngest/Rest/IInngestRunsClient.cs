// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

namespace Inngest;

// Function runs through the Inngest REST API, authenticated with the app's signing key. Inngest caches these
// answers for a few seconds (a run for 3, the runs of an event for 15), so a run can look a little behind; one that
// was just started may not be listed yet. Failures throw InngestException with Inngest's message.
public interface IInngestRunsClient
{
    /// <summary>Gets a run with its status and, once it has ended, its output.</summary>
    Task<FunctionRun> GetRunAsync(string runId, CancellationToken cancellationToken = default);

    /// <summary>Lists the runs an event started, by the event id that sending it returned.</summary>
    Task<IReadOnlyList<FunctionRun>> GetEventRunsAsync(string eventId, CancellationToken cancellationToken = default);

    /// <summary>Cancels a run: a step that is running finishes, and no further steps run.</summary>
    Task CancelRunAsync(string runId, CancellationToken cancellationToken = default);
}
