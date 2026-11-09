// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using System.Globalization;
using Microsoft.Extensions.Options;
using Sentry;
using Sentry.Extensibility;

namespace Inngest.Sentry;

// Reports one call to Sentry: a transaction for the call and a span for each step it executes, as the TypeScript
// middleware's request and step spans, and its errors. Middleware is scoped to the call, so the instance keeps the
// steps the call executed, as breadcrumbs for the events it reports, until its scope ends, and finishes the transaction
// and flushes then. The transaction cannot end sooner: no hook marks the end of a call that reports new steps.
//
// Sentry's scope follows the async flow. One is pushed around the function and around every step handler, which tags
// what the app reports there itself. The hooks that see the errors run outside those flows, so a report pushes a scope
// of its own, and the event takes a copy of it.
internal sealed class SentryMiddleware(IInngestClient client, IOptions<SentryReportingOptions> options, IHub? registeredHub = null)
    : InngestMiddleware, IAsyncDisposable, IDisposable
{
    private const string StepCategory = "inngest.step";

    // As many breadcrumbs as Sentry keeps by default, so that a call with many steps does not outgrow a scope.
    private const int MaxBreadcrumbs = 100;

    private readonly SentryReportingOptions _options = options.Value;
    private readonly Queue<Breadcrumb> _breadcrumbs = new();

    // A step handler finishes off the engine's scheduler, so the step spans and the outcome are shared under a lock.
    private readonly object _gate = new();
    private readonly Dictionary<string, ISpan> _steps = new(StringComparer.Ordinal);
    private FunctionContext? _context;
    private ITransactionTracer? _transaction;
    private bool _failed;

    private IHub Hub => registeredHub ?? HubAdapter.Instance;

    public override ValueTask OnMemoizationEndAsync(FunctionContext context)
    {
        Begin(context);
        return ValueTask.CompletedTask;
    }

    public override ValueTask OnRunStartAsync(FunctionContext context)
    {
        Begin(context);
        return ValueTask.CompletedTask;
    }

    // The scope ends with this method for the engine, which goes on in the flow it called from. The function started
    // inside keeps the one it started in.
    public override Task<object?> WrapFunctionAsync(FunctionContext context, Func<Task<object?>> next)
    {
        Begin(context);
        using var scope = Enter(context);
        return next();
    }

    // Only the steps the app executes have a handler, and so a span: a step that Inngest carries out, such as a sleep,
    // is not run in the call that finds it.
    public override async Task<object?> WrapStepHandlerAsync(FunctionContext context, StepInfo step, Func<Task<object?>> next)
    {
        var span = StartStep(Begin(context), step);
        using var scope = Enter(context);
        try
        {
            var output = await next().ConfigureAwait(false);
            span?.Finish(SpanStatus.Ok);
            return output;
        }
        catch (Exception exception)
        {
            span?.Finish(exception, SpanStatus.InternalError);
            throw;
        }
    }

    public override ValueTask OnStepCompleteAsync(FunctionContext context, StepInfo step, object? output)
    {
        AddBreadcrumb(step, BreadcrumbLevel.Info);
        return ValueTask.CompletedTask;
    }

    public override ValueTask OnStepErrorAsync(FunctionContext context, StepInfo step, Exception exception, bool isFinalAttempt)
    {
        Fail();
        AddBreadcrumb(step, BreadcrumbLevel.Error);
        if (_options.CaptureStepErrors && IsReported(isFinalAttempt))
        {
            Report(context, exception, "step", step);
        }

        return ValueTask.CompletedTask;
    }

    public override ValueTask OnRunErrorAsync(FunctionContext context, Exception exception, bool isFinalAttempt)
    {
        Fail();
        if (IsReported(isFinalAttempt))
        {
            Report(context, exception, "run", step: null);
        }

        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        End();
        var hub = Hub;
        if (!_options.DisableAutomaticFlush && hub.IsEnabled)
        {
            await hub.FlushAsync(_options.FlushTimeout).ConfigureAwait(false);
        }
    }

    // Hosts dispose the scope of a call asynchronously. One that does not still gets its events sent, by waiting.
    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    private bool IsReported(bool isFinalAttempt) => !_options.OnlyCaptureFinalAttempt || isFinalAttempt;

    // The call's transaction, started by the first hook that sees the call, in the trace of the run.
    private ITransactionTracer? Begin(FunctionContext context)
    {
        lock (_gate)
        {
            if (ReferenceEquals(context, _context))
            {
                return _transaction;
            }
        }

        // A host that resolved the middleware outside a scope of its own reuses it for the next call.
        End();
        var hub = Hub;
        ITransactionTracer? transaction = null;
        if (hub.IsEnabled)
        {
            var trace = hub.ContinueTrace(RunTrace.HeaderOf(context.RunId), baggageHeader: null, $"inngest:{context.FunctionId}", "request");
            transaction = hub.StartTransaction(trace, new Dictionary<string, object?>());
            foreach (var (key, value) in TagsOf(context))
            {
                transaction.SetTag(key, value);
            }
        }

        lock (_gate)
        {
            _context = context;
            _transaction = transaction;
            _failed = false;
        }

        return transaction;
    }

    private ISpan? StartStep(ITransactionTracer? transaction, StepInfo step)
    {
        if (transaction is null)
        {
            return null;
        }

        var span = transaction.StartChild("step", step.Name ?? step.Id);
        span.SetTag(Tags.StepName, step.Name ?? step.Id);
        span.SetTag(Tags.StepType, step.Type.ToString());
        lock (_gate)
        {
            _steps[step.HashedId] = span;
        }

        return span;
    }

    private void Fail()
    {
        lock (_gate)
        {
            _failed = true;
        }
    }

    // Finishes the transaction, and the spans of steps that are still open, as when the call was cancelled.
    private void End()
    {
        ITransactionTracer? transaction;
        List<ISpan> open;
        bool failed;
        lock (_gate)
        {
            transaction = _transaction;
            failed = _failed;
            open = [.. _steps.Values.Where(span => !span.IsFinished)];
            _steps.Clear();
            _context = null;
            _transaction = null;
        }

        foreach (var span in open)
        {
            span.Finish(SpanStatus.Cancelled);
        }

        transaction?.Finish(failed ? SpanStatus.InternalError : SpanStatus.Ok);
    }

    // Tags the scope with the call and puts it in the trace of the run. Sentry.AspNetCore locks the scope of a request,
    // and a push there creates nothing, so this tags the request's scope, which is the call's as well.
    private IDisposable Enter(FunctionContext context)
    {
        var hub = Hub;
        var scope = hub.PushScope();
        if (hub.IsEnabled)
        {
            hub.ContinueTrace(RunTrace.HeaderOf(context.RunId), baggageHeader: null);
            hub.ConfigureScope(current => Tag(current, context));
        }

        return scope;
    }

    private void Tag(Scope scope, FunctionContext context)
    {
        foreach (var (key, value) in TagsOf(context))
        {
            scope.SetTag(key, value);
        }
    }

    private List<KeyValuePair<string, string>> TagsOf(FunctionContext context)
    {
        var tags = new List<KeyValuePair<string, string>>
        {
            new(Tags.ClientId, client.AppId),
            new(Tags.FunctionId, $"{client.AppId}-{context.FunctionId}"),
            new(Tags.RunId, context.RunId),
            new(Tags.Attempt, context.Attempt.ToString(CultureInfo.InvariantCulture)),
            new(Tags.EventName, context.EventName),
        };
        if (context.EventId is { } eventId)
        {
            tags.Add(new(Tags.EventId, eventId));
        }

        return tags;
    }

    private void Report(FunctionContext context, Exception exception, string source, StepInfo? step)
    {
        var hub = Hub;
        if (!hub.IsEnabled)
        {
            return;
        }

        ITransactionTracer? transaction;
        ISpan? stepSpan = null;
        lock (_gate)
        {
            transaction = _transaction;
            if (step is not null)
            {
                _steps.TryGetValue(step.HashedId, out stepSpan);
            }
        }

        using var scope = Enter(context);
        hub.CaptureException(exception, copy =>
        {
            // The request that carries the call has a transaction of its own, whose trace the event would join. The
            // event joins the call's transaction instead, under the span of the step that failed.
            copy.Transaction = transaction;
            copy.Span = stepSpan;
            copy.TransactionName = $"inngest:{context.FunctionId}";
            copy.SetTag(Tags.ErrorSource, source);
            if (step is not null)
            {
                copy.SetTag(Tags.StepName, step.Name ?? step.Id);
            }

            foreach (var breadcrumb in _breadcrumbs)
            {
                copy.AddBreadcrumb(breadcrumb);
            }
        });
    }

    private void AddBreadcrumb(StepInfo step, BreadcrumbLevel level)
    {
        if (_breadcrumbs.Count == MaxBreadcrumbs)
        {
            _breadcrumbs.Dequeue();
        }

        var data = new Dictionary<string, string> { ["type"] = step.Type.ToString() };
        _breadcrumbs.Enqueue(new Breadcrumb(step.Name ?? step.Id, "default", data, StepCategory, level));
    }

    // The tag names are the TypeScript middleware's, with the attempt added.
    private static class Tags
    {
        public const string ClientId = "inngest.client.id";
        public const string FunctionId = "inngest.function.id";
        public const string RunId = "inngest.run.id";
        public const string Attempt = "inngest.attempt";
        public const string EventName = "inngest.event.name";
        public const string EventId = "inngest.event.id";
        public const string ErrorSource = "inngest.error.source";
        public const string StepName = "inngest.step.name";
        public const string StepType = "inngest.step.type";
    }
}
