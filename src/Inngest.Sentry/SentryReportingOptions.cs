// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

namespace Inngest;

/// <summary>
/// Settings of the middleware that AddSentryReporting adds. Sentry itself is set up as usual, with SentrySdk.Init or
/// UseSentry; none of these settings reach it.
/// </summary>
public sealed class SentryReportingOptions
{
    /// <summary>
    /// Leaves flushing to Sentry. By default the middleware flushes at the end of every call, so that events are sent
    /// before the runtime stops, which serverless runtimes may do once a call has answered.
    /// </summary>
    public bool DisableAutomaticFlush { get; set; }

    /// <summary>
    /// How long the flush at the end of a call waits for events to be sent. Two seconds, as in Sentry's own
    /// FlushTimeout.
    /// </summary>
    public TimeSpan FlushTimeout { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Reports an error of a function or a step only on its final attempt, when no retry follows because the retries
    /// are used up or the error is not retriable. Errors that Inngest retries are then left out, which keeps transient
    /// failures from making noise. Defaults to true.
    /// </summary>
    public bool OnlyCaptureFinalAttempt { get; set; } = true;

    /// <summary>
    /// Reports an error of a step as an event of its own, besides the error of the function. That shows which step
    /// failed, but an error that reaches the function from its step is reported twice. Without it, steps leave only
    /// breadcrumbs on the events that follow. Defaults to false.
    /// </summary>
    public bool CaptureStepErrors { get; set; }
}
