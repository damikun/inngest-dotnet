// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using Microsoft.Extensions.Logging;

namespace Inngest.Execution;

internal static partial class Log
{
    [LoggerMessage(100, LogLevel.Warning,
        "Function {FunctionId} never reached step {HashedId}, which run {RunId} completed before steps the function did reach, so its " +
        "code probably changed between requests. Memoized results were handed back in the order the steps were reached instead.")]
    public static partial void StepOrderChanged(ILogger logger, string functionId, string hashedId, string runId);

    [LoggerMessage(101, LogLevel.Warning,
        "Function {FunctionId} uses step id \"{StepId}\" again after other work in run {RunId}, so it was numbered automatically. " +
        "Give steps in parallel branches unique ids; otherwise they can be matched to the wrong results.")]
    public static partial void StepIdReused(ILogger logger, string functionId, string stepId, string runId);

    [LoggerMessage(102, LogLevel.Warning,
        "Inngest asked run {RunId} of function {FunctionId} to execute step {HashedId}, which the function did not reach within {Timeout}.")]
    public static partial void StepNotFound(ILogger logger, string runId, string functionId, string hashedId, TimeSpan timeout);

    [LoggerMessage(103, LogLevel.Error, "Could not load the state of run {RunId} from the Inngest API.")]
    public static partial void RunStateUnavailable(ILogger logger, string runId, Exception exception);

    [LoggerMessage(104, LogLevel.Error, "Could not start run {RunId} of function {FunctionId}.")]
    public static partial void InvocationFailed(ILogger logger, string runId, string functionId, Exception exception);

    [LoggerMessage(105, LogLevel.Error, "Middleware {Middleware} failed in {Hook}; the run continues.")]
    public static partial void MiddlewareFailed(ILogger logger, string middleware, string hook, Exception exception);

    [LoggerMessage(106, LogLevel.Warning, "Run {RunId} of function {FunctionId} threw on attempt {Attempt}; Inngest retries it.")]
    public static partial void RunErrored(ILogger logger, string runId, string functionId, int attempt, Exception exception);

    [LoggerMessage(107, LogLevel.Error, "Run {RunId} of function {FunctionId} failed on attempt {Attempt} and is not retried.")]
    public static partial void RunFailed(ILogger logger, string runId, string functionId, int attempt, Exception exception);

    [LoggerMessage(108, LogLevel.Warning, "Step {StepId} of run {RunId} of function {FunctionId} threw on attempt {Attempt}; Inngest retries it.")]
    public static partial void StepErrored(ILogger logger, string stepId, string runId, string functionId, int attempt, Exception exception);

    [LoggerMessage(109, LogLevel.Error, "Step {StepId} of run {RunId} of function {FunctionId} failed on attempt {Attempt} and is not retried.")]
    public static partial void StepFailed(ILogger logger, string stepId, string runId, string functionId, int attempt, Exception exception);

    [LoggerMessage(140, LogLevel.Debug, "Saved {Count} step(s) of run {RunId} of function {FunctionId} to Inngest; the call goes on.")]
    public static partial void Checkpointed(ILogger logger, int count, string runId, string functionId);

    [LoggerMessage(141, LogLevel.Warning,
        "Could not save {Count} step(s) of run {RunId} of function {FunctionId} to Inngest, so the call answers with them instead and Inngest calls again.")]
    public static partial void CheckpointFailed(ILogger logger, int count, string runId, string functionId, Exception exception);

    [LoggerMessage(142, LogLevel.Warning,
        "Inngest dispatched run {RunId} of function {FunctionId} again while this call was running, so the call stops without running more steps.")]
    public static partial void StaleDispatch(ILogger logger, string runId, string functionId, Exception exception);
}
