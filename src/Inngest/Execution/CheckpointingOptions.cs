// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using System.Globalization;

namespace Inngest;

/// <summary>
/// Runs the steps of a function one after another in the same call from Inngest and saves each to Inngest as it
/// completes, instead of ending the call after every step. The run goes back to one call per step at sleeps, waits,
/// invokes, parallel steps and step errors, and once MaxRuntime has passed.
/// </summary>
public sealed record CheckpointingOptions
{
    /// <summary>
    /// False turns checkpointing off for one function when InngestOptions turns it on for all of them.
    /// </summary>
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// How long one call may keep running steps before it leaves the next step to Inngest. Null means 10 seconds for
    /// the serve endpoint and 5 minutes for Connect workers; Timeout.InfiniteTimeSpan removes the limit. Keep it well
    /// below the request timeout of the host and of any proxy in front of it.
    /// </summary>
    public TimeSpan? MaxRuntime { get; init; }

    /// <summary>
    /// How many completed steps are saved to Inngest together. With 1, each step is saved before the next one runs;
    /// more saves requests, but steps not yet saved run again if the call is lost.
    /// </summary>
    public int BufferedSteps { get; init; } = 1;

    /// <summary>
    /// The longest a completed step waits to be saved while BufferedSteps is not reached. Null waits until the buffer
    /// is full or the call ends.
    /// </summary>
    public TimeSpan? MaxInterval { get; init; }

    // The first setting Inngest cannot work with, named after the property, or null when all are valid.
    internal string? FindProblem()
    {
        if (MaxRuntime is { } maxRuntime && maxRuntime <= TimeSpan.Zero && maxRuntime != Timeout.InfiniteTimeSpan)
        {
            return $"MaxRuntime must be greater than zero, or Timeout.InfiniteTimeSpan for no limit, but was {Describe(maxRuntime)}.";
        }

        if (BufferedSteps < 1)
        {
            return $"BufferedSteps must be at least 1, but was {BufferedSteps}.";
        }

        if (MaxInterval is { } maxInterval && maxInterval <= TimeSpan.Zero)
        {
            return $"MaxInterval must be greater than zero, but was {Describe(maxInterval)}.";
        }

        return null;
    }

    private static string Describe(TimeSpan duration) => duration.ToString("c", CultureInfo.InvariantCulture);
}
