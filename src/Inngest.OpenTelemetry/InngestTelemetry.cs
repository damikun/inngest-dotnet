// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Reflection;

namespace Inngest.OpenTelemetry;

// The ActivitySource, the Meter and its instruments. Span and attribute names are the ones the TypeScript SDK and the
// Inngest server use (inngest.execution, sdk.run.id, inngest.step.id); errors follow OpenTelemetry's conventions
// (status, error.type and an exception event).
internal sealed class InngestTelemetry : IDisposable
{
    public const string CallSpan = "inngest.execution";
    public const string StepSpan = "inngest.step";

    public static readonly string Version = typeof(InngestTelemetry).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.0.0";

    public static readonly ActivitySource Source = new(InngestInstrumentation.ActivitySourceName, Version);

    // From a few milliseconds to the minutes a step that calls a slow service can take.
    private static readonly double[] DurationBuckets = [0.005, 0.01, 0.025, 0.05, 0.075, 0.1, 0.25, 0.5, 0.75, 1, 2.5, 5, 7.5, 10, 30, 60, 120, 300];

    private readonly ConcurrentDictionary<string, string> _slugs = new(StringComparer.Ordinal);
    private readonly string _appId;
    private readonly Meter? _ownMeter;

    // Hosts register an IMeterFactory, which keeps the meter apart per container; without one the meter is our own.
    public InngestTelemetry(IInngestClient client, IMeterFactory? meterFactory = null)
    {
        _appId = client.AppId;
        var options = new MeterOptions(InngestInstrumentation.MeterName) { Version = Version };
        Meter meter;
        if (meterFactory is null)
        {
            meter = _ownMeter = new Meter(options);
        }
        else
        {
            meter = meterFactory.Create(options);
        }

        var duration = new InstrumentAdvice<double> { HistogramBucketBoundaries = DurationBuckets };
        RunsStarted = meter.CreateCounter<long>("inngest.run.started", "{run}", "Function runs started.");
        RunsCompleted = meter.CreateCounter<long>("inngest.run.completed", "{run}", "Function runs whose function returned.");
        RunsFailed = meter.CreateCounter<long>("inngest.run.failed", "{run}", "Function runs that failed for good.");
        Retries = meter.CreateCounter<long>("inngest.retries", "{retry}", "Failed attempts of functions and steps that Inngest retries.");
        ExecutionDuration = meter.CreateHistogram("inngest.execution.duration", "s", "Duration of the calls Inngest makes to run functions.", tags: null, duration);
        StepDuration = meter.CreateHistogram("inngest.step.duration", "s", "Duration of the steps the app executed.", tags: null, duration);
    }

    public Counter<long> RunsStarted { get; }

    public Counter<long> RunsCompleted { get; }

    public Counter<long> RunsFailed { get; }

    public Counter<long> Retries { get; }

    public Histogram<double> ExecutionDuration { get; }

    public Histogram<double> StepDuration { get; }

    public static Activity? StartCall(string slug, FunctionContext context)
    {
        var activity = Source.StartActivity(CallSpan, ActivityKind.Internal);
        if (activity is { IsAllDataRequested: true })
        {
            activity.SetTag(Attributes.FunctionSlug, slug);
            activity.SetTag(Attributes.RunId, context.RunId);
            activity.SetTag(Attributes.Attempt, context.Attempt);
            activity.SetTag(Attributes.EventName, context.EventName);
            activity.SetTag(Attributes.EventId, context.EventId);

            // Inngest's own ids of the function and the app come in the trace state it sends with every call, which
            // the span inherits from the request's span, the same place the TypeScript SDK reads them from.
            if (activity.TraceStateString is { Length: > 0 } traceState)
            {
                activity.SetTag(Attributes.FunctionId, TraceStateValue(traceState, "inngest@fn="));
                activity.SetTag(Attributes.AppId, TraceStateValue(traceState, "inngest@app="));
            }
        }

        return activity;
    }

    // Under the call's span when there is one, else under whatever is current.
    public static Activity? StartStep(Activity? call, string slug, FunctionContext context, StepInfo step)
    {
        var activity = Source.StartActivity(StepSpan, ActivityKind.Internal, call?.Context ?? default);
        if (activity is { IsAllDataRequested: true })
        {
            activity.SetTag(Attributes.StepId, step.Id);
            activity.SetTag(Attributes.StepType, TypeName(step.Type));
            activity.SetTag(Attributes.StepAttempt, context.Attempt);
            activity.SetTag(Attributes.RunId, context.RunId);
            activity.SetTag(Attributes.FunctionSlug, slug);
        }

        return activity;
    }

    public static void RecordError(Activity? activity, Exception exception)
    {
        if (activity is null)
        {
            return;
        }

        activity.SetStatus(ActivityStatusCode.Error, exception.Message);
        activity.SetTag(Attributes.ErrorType, exception.GetType().FullName);
        activity.AddException(exception);
    }

    // Dashboards and alerts key on these names, so a test checks that every step type has one of its own.
    public static string TypeName(StepType type) => type switch
    {
        StepType.Run => "run",
        StepType.Sleep => "sleep",
        StepType.WaitForEvent => "wait_for_event",
        StepType.Invoke => "invoke",
        StepType.SendEvent => "send_event",
        StepType.WaitForSignal => "wait_for_signal",
        StepType.SendSignal => "send_signal",
        StepType.Fetch => "fetch",
        StepType.AIInfer => "ai_infer",
        StepType.AIWrap => "ai_wrap",
        _ => type.ToString(),
    };

    // The id Inngest knows the function by.
    public string SlugOf(string functionId) => _slugs.GetOrAdd(functionId, static (id, appId) => $"{appId}-{id}", _appId);

    // A W3C trace state is a comma-separated list of key=value entries.
    private static string? TraceStateValue(string traceState, string prefix)
    {
        for (var start = 0; start < traceState.Length;)
        {
            var end = traceState.IndexOf(',', start);
            if (end < 0)
            {
                end = traceState.Length;
            }

            var entry = traceState.AsSpan(start, end - start).Trim();
            if (entry.StartsWith(prefix, StringComparison.Ordinal))
            {
                return entry[prefix.Length..].ToString();
            }

            start = end + 1;
        }

        return null;
    }

    public void Dispose() => _ownMeter?.Dispose();

    public static class Attributes
    {
        // The server's name for "<app id>-<function id>". sys.function.id and sys.app.id are the server's UUIDs, which
        // only the trace state of a call has.
        public const string FunctionSlug = "sys.function.slug";
        public const string FunctionId = "sys.function.id";
        public const string AppId = "sys.app.id";
        public const string RunId = "sdk.run.id";
        public const string Attempt = "inngest.attempt";
        public const string EventName = "inngest.event.name";
        public const string EventId = "inngest.event.id";
        public const string MemoizedSteps = "inngest.memoized_steps";
        public const string StepId = "inngest.step.id";
        public const string StepType = "inngest.step.type";
        public const string StepAttempt = "inngest.step.attempt";
        public const string ErrorType = "error.type";
    }
}
