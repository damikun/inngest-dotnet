// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.

namespace Inngest;

/// <summary>
/// What the health check added with <c>AddInngest</c> on <c>IHealthChecksBuilder</c> requires of the app, and how often
/// it asks Inngest.
/// </summary>
public sealed class InngestHealthCheckOptions
{
    /// <summary>
    /// Whether the check fails while no event key is configured. An app that only receives calls from Inngest needs
    /// none, so this is off by default; turn it on for an app that sends events. The dev server accepts any key, so
    /// dev mode is never held to it. The key is only checked for being set: Inngest has no request that validates an
    /// event key without sending an event.
    /// </summary>
    public bool RequireEventKey { get; set; }

    /// <summary>
    /// Whether the check also reads what Inngest knows about the app, from its Get app endpoint, which Inngest marks
    /// Beta, and fails while the app is not registered, is archived, failed its latest sync, or differs from this
    /// instance in its app version (when <see cref="InngestOptions.AppVersion" /> is set) or in the number of functions.
    /// It costs a second request when the answer is renewed. It is off by default, and it does not belong in the
    /// readiness probe of a deployment: the app is synced after its new instances serve, so the check fails after every
    /// deploy that changes the functions, until the sync. Use it to watch for a sync that was forgotten, or in a step
    /// of the pipeline after the sync.
    /// </summary>
    public bool CheckSync { get; set; }

    /// <summary>
    /// How long the answer of a successful request to Inngest is reused before a probe asks again. Probes in between
    /// get the stored result, so a probe every second does not send a request every second. Zero asks on every probe.
    /// The default is 30 seconds.
    /// </summary>
    public TimeSpan CacheDuration { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How long the answer of a failed request is reused, never longer than <see cref="CacheDuration" />. It is shorter
    /// on purpose: a pod that is not ready stays out of rotation for this long after Inngest is reachable again. Zero
    /// asks on every probe. The default is 5 seconds.
    /// </summary>
    public TimeSpan FailureCacheDuration { get; set; } = TimeSpan.FromSeconds(5);
}
