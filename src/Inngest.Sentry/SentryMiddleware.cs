// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using System.Globalization;
using Microsoft.Extensions.Options;
using Sentry;
using Sentry.Extensibility;

namespace Inngest.Sentry;

// Reports the errors of one call to Sentry. Middleware is scoped to the call, so the instance keeps the steps the call
// executed, as breadcrumbs for the events it reports, until its scope ends, and flushes then.
//
// Sentry's scope follows the async flow, so one is pushed around the function and around every step handler, which
// tags what the app reports there itself. The hooks that see the errors run outside those flows. A report therefore
// pushes a scope of its own, and the event takes a copy of it.
internal sealed class SentryMiddleware(IInngestClient client, IOptions<SentryReportingOptions> options, IHub? hub = null)
    : InngestMiddleware, IAsyncDisposable, IDisposable
{
    private const string ClientId = "inngest.client.id";
    private const string FunctionId = "inngest.function.id";
    private const string RunId = "inngest.run.id";
    private const string Attempt = "inngest.attempt";
    private const string EventName = "inngest.event.name";
    private const string EventId = "inngest.event.id";
    private const string ErrorSource = "inngest.error.source";
    private const string StepName = "inngest.step.name";
    private const string StepCategory = "inngest.step";

    // As many as Sentry keeps by default, so that a call with many steps does not outgrow what a scope holds.
    private const int MaxBreadcrumbs = 100;

    private readonly SentryReportingOptions _options = options.Value;
    private readonly Queue<Breadcrumb> _breadcrumbs = new();

    private IHub Hub => hub ?? HubAdapter.Instance;

    // The scope ends with this method for the engine, which goes on in the flow it called from. The function started
    // inside keeps the one it started in.
    public override Task<object?> WrapFunctionAsync(FunctionContext context, Func<Task<object?>> next)
    {
        using var scope = Enter(context);
        return next();
    }

    public override Task<object?> WrapStepHandlerAsync(FunctionContext context, StepInfo step, Func<Task<object?>> next)
    {
        using var scope = Enter(context);
        return next();
    }

    public override ValueTask OnStepCompleteAsync(FunctionContext context, StepInfo step, object? output)
    {
        AddBreadcrumb(step, BreadcrumbLevel.Info);
        return ValueTask.CompletedTask;
    }

    public override ValueTask OnStepErrorAsync(FunctionContext context, StepInfo step, Exception exception, bool isFinalAttempt)
    {
        AddBreadcrumb(step, BreadcrumbLevel.Error);
        if (_options.CaptureStepErrors && IsReported(isFinalAttempt))
        {
            Report(context, exception, "step", step);
        }

        return ValueTask.CompletedTask;
    }

    public override ValueTask OnRunErrorAsync(FunctionContext context, Exception exception, bool isFinalAttempt)
    {
        if (IsReported(isFinalAttempt))
        {
            Report(context, exception, "run", step: null);
        }

        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        var hub = Hub;
        if (!_options.DisableAutomaticFlush && hub.IsEnabled)
        {
            await hub.FlushAsync(_options.FlushTimeout).ConfigureAwait(false);
        }
    }

    // Hosts dispose the scope of a call asynchronously. One that does not still gets its events sent, by waiting.
    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    private bool IsReported(bool isFinalAttempt) => !_options.OnlyCaptureFinalAttempt || isFinalAttempt;

    // Tags the scope with the call and puts it in the trace of the run. In a scope that Sentry.AspNetCore locks for the
    // request, a push creates nothing and this tags the request's scope, which is the call's as well.
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
        scope.SetTag(ClientId, client.AppId);
        scope.SetTag(FunctionId, $"{client.AppId}-{context.FunctionId}");
        scope.SetTag(RunId, context.RunId);
        scope.SetTag(Attempt, context.Attempt.ToString(CultureInfo.InvariantCulture));
        scope.SetTag(EventName, context.EventName);
        if (context.EventId is { } eventId)
        {
            scope.SetTag(EventId, eventId);
        }
    }

    private void Report(FunctionContext context, Exception exception, string source, StepInfo? step)
    {
        var hub = Hub;
        if (!hub.IsEnabled)
        {
            return;
        }

        using var scope = Enter(context);
        hub.CaptureException(exception, copy =>
        {
            // The request that carries the call has a transaction of its own, whose trace the event would join.
            copy.Transaction = null;
            copy.TransactionName = $"inngest:{context.FunctionId}";
            copy.SetTag(ErrorSource, source);
            if (step is not null)
            {
                copy.SetTag(StepName, step.Name ?? step.Id);
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
}
