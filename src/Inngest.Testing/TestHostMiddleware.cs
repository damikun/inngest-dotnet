// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using Inngest.Middleware;

namespace Inngest.Testing;

// The host's hooks into the engine. It is registered after the app's middleware and is innermost, so the app's
// wrappers and a function's own wrap it: a mocked run step is answered where its handler would run, a run's
// exceptions are kept as they were thrown, and sent events are recorded instead of leaving the process.
internal sealed class TestHostMiddleware(InngestTestHost host) : InngestMiddleware, IInnermostMiddleware
{
    public override Task<object?> WrapStepHandlerAsync(FunctionContext context, StepInfo step, Func<Task<object?>> next)
    {
        if (RunDriver.Current?.FindRunMock(step) is not { } mock)
        {
            return next();
        }

        try
        {
            return Task.FromResult(mock.Answer(context.Attempt));
        }
        catch (Exception exception)
        {
            return Task.FromException<object?>(exception);
        }
    }

    public override ValueTask OnStepErrorAsync(FunctionContext context, StepInfo step, Exception exception, bool isFinalAttempt)
    {
        RunDriver.Current?.CaptureStepError(step.HashedId, exception);
        return ValueTask.CompletedTask;
    }

    public override ValueTask OnRunErrorAsync(FunctionContext context, Exception exception, bool isFinalAttempt)
    {
        RunDriver.Current?.CaptureRunError(exception);
        return ValueTask.CompletedTask;
    }

    public override Task<SendEventsResult> WrapSendEventAsync(
        FunctionContext? context,
        IReadOnlyList<InngestEvent> events,
        Func<IReadOnlyList<InngestEvent>, Task<SendEventsResult>> next,
        CancellationToken cancellationToken)
    {
        try
        {
            return Task.FromResult(host.Record(events, RunDriver.Current));
        }
        catch (Exception exception)
        {
            return Task.FromException<SendEventsResult>(exception);
        }
    }
}
