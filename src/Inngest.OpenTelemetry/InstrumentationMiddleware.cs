// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using System.Diagnostics;

namespace Inngest.OpenTelemetry;

// Traces and measures one call from Inngest. Middleware is scoped to the call, so the instance holds the call's span
// from the first hook that sees the call until the scope ends. It cannot end sooner: no hook marks the end of a call
// that reports new steps, and the next() of WrapFunctionAsync never returns in such a call.
//
// Memoized steps get no spans and count as no steps, because nothing runs for them; the call's span says how many it
// replayed. The engine runs every hook of a call one after the other, so the fields need no locks.
internal sealed class InstrumentationMiddleware(InngestTelemetry telemetry) : InngestMiddleware, IDisposable
{
    private FunctionContext? _context;
    private Activity? _call;
    private string _slug = string.Empty;
    private long _startedAt;
    private string? _errorType;
    private int _memoized;

    // New steps found in this call that it did not execute, which Inngest still has to carry out.
    private int _unexecuted;

    public override ValueTask OnMemoizationEndAsync(FunctionContext context)
    {
        Begin(context);
        return ValueTask.CompletedTask;
    }

    // Only the first call of a run sees this.
    public override ValueTask OnRunStartAsync(FunctionContext context)
    {
        Begin(context);
        telemetry.RunsStarted.Add(1, new KeyValuePair<string, object?>(InngestTelemetry.Attributes.FunctionSlug, _slug));
        return ValueTask.CompletedTask;
    }

    public override Task<object?> WrapFunctionAsync(FunctionContext context, Func<Task<object?>> next)
    {
        // The activities the function starts outside its steps belong to the call. Restoring the previous activity
        // afterwards keeps the call's span from leaking into the caller.
        var call = Begin(context);
        var previous = Activity.Current;
        Activity.Current = call ?? previous;
        try
        {
            return next();
        }
        finally
        {
            Activity.Current = previous;
        }
    }

    public override Task<object?> WrapStepAsync(FunctionContext context, StepInfo step, Func<Task<object?>> next)
    {
        if (step.Memoized)
        {
            _memoized++;
        }
        else
        {
            _unexecuted++;
        }

        return next();
    }

    public override async Task<object?> WrapStepHandlerAsync(FunctionContext context, StepInfo step, Func<Task<object?>> next)
    {
        var call = Begin(context);
        if (_unexecuted > 0)
        {
            _unexecuted--;
        }

        using var activity = InngestTelemetry.StartStep(call, _slug, context, step);
        var startedAt = Stopwatch.GetTimestamp();
        string? errorType = null;
        try
        {
            return await next().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            errorType = exception.GetType().FullName;
            InngestTelemetry.RecordError(activity, exception);
            throw;
        }
        finally
        {
            var tags = new TagList
            {
                { InngestTelemetry.Attributes.FunctionSlug, _slug },
                { InngestTelemetry.Attributes.StepType, InngestTelemetry.TypeName(step.Type) },
            };
            if (errorType is not null)
            {
                tags.Add(InngestTelemetry.Attributes.ErrorType, errorType);
            }

            telemetry.StepDuration.Record(Stopwatch.GetElapsedTime(startedAt).TotalSeconds, tags);
        }
    }

    public override ValueTask OnStepErrorAsync(FunctionContext context, StepInfo step, Exception exception, bool isFinalAttempt)
    {
        Begin(context);
        if (!isFinalAttempt)
        {
            telemetry.Retries.Add(
                1,
                new KeyValuePair<string, object?>(InngestTelemetry.Attributes.FunctionSlug, _slug),
                new KeyValuePair<string, object?>(InngestTelemetry.Attributes.StepType, InngestTelemetry.TypeName(step.Type)),
                new KeyValuePair<string, object?>(InngestTelemetry.Attributes.ErrorType, exception.GetType().FullName));
        }

        return ValueTask.CompletedTask;
    }

    // A function that returns while steps it never awaited are still to run is called again once they ran, and returns
    // again; only the return that ends the run counts. Steps that ran in the call itself, as with checkpointing, do not
    // keep the run open.
    public override ValueTask OnRunCompleteAsync(FunctionContext context, object? output)
    {
        Begin(context);
        if (_unexecuted == 0)
        {
            telemetry.RunsCompleted.Add(1, new KeyValuePair<string, object?>(InngestTelemetry.Attributes.FunctionSlug, _slug));
        }

        return ValueTask.CompletedTask;
    }

    public override ValueTask OnRunErrorAsync(FunctionContext context, Exception exception, bool isFinalAttempt)
    {
        var call = Begin(context);
        _errorType = exception.GetType().FullName;
        InngestTelemetry.RecordError(call, exception);
        var function = new KeyValuePair<string, object?>(InngestTelemetry.Attributes.FunctionSlug, _slug);
        var error = new KeyValuePair<string, object?>(InngestTelemetry.Attributes.ErrorType, _errorType);
        if (isFinalAttempt)
        {
            telemetry.RunsFailed.Add(1, function, error);
        }
        else
        {
            telemetry.Retries.Add(1, function, error);
        }

        return ValueTask.CompletedTask;
    }

    public void Dispose() => End();

    private Activity? Begin(FunctionContext context)
    {
        if (ReferenceEquals(context, _context))
        {
            return _call;
        }

        // A host that resolved the middleware outside a scope of its own reuses it for the next call.
        End();
        _context = context;
        _slug = telemetry.SlugOf(context.FunctionId);
        _startedAt = Stopwatch.GetTimestamp();

        // Starting an activity makes it current, which only WrapFunctionAsync should do, and only around the function.
        var previous = Activity.Current;
        _call = InngestTelemetry.StartCall(_slug, context);
        Activity.Current = previous;
        return _call;
    }

    private void End()
    {
        if (_context is null)
        {
            return;
        }

        var tags = new TagList { { InngestTelemetry.Attributes.FunctionSlug, _slug } };
        if (_errorType is not null)
        {
            tags.Add(InngestTelemetry.Attributes.ErrorType, _errorType);
        }

        telemetry.ExecutionDuration.Record(Stopwatch.GetElapsedTime(_startedAt).TotalSeconds, tags);
        if (_call is not null)
        {
            if (_call.IsAllDataRequested)
            {
                _call.SetTag(InngestTelemetry.Attributes.MemoizedSteps, _memoized);
            }

            // Stopping an activity makes its parent current, which must not change the flow that disposes the scope.
            var current = Activity.Current;
            _call.Stop();
            Activity.Current = current;
        }

        _context = null;
        _call = null;
        _errorType = null;
        _memoized = 0;
        _unexecuted = 0;
    }
}
