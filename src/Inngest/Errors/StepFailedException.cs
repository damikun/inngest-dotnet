// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

namespace Inngest;

/// <summary>
/// Thrown at a step's call site when the step failed all of its attempts, during that run and every later replay.
/// Catch it to recover; left uncaught, the function fails without further retries. The inner exception is the
/// recorded cause of the failure, as a FunctionErrorException.
/// </summary>
public sealed class StepFailedException : Exception
{
    public StepFailedException(string stepId, FunctionError error)
        : base(error?.Message, error?.Cause is { } cause ? new FunctionErrorException(cause) : null)
    {
        ArgumentNullException.ThrowIfNull(error);
        StepId = stepId;
        Error = error;
    }

    public string StepId { get; }

    /// <summary>
    /// The original failure as Inngest recorded it, including the exception type name, stack trace and cause.
    /// </summary>
    public FunctionError Error { get; }
}
