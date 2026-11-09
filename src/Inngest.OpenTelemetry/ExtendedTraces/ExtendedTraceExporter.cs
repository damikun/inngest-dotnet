// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using Inngest.Client;
using Inngest.Configuration;
using Inngest.Internal;
using Microsoft.Extensions.Logging;

namespace Inngest.OpenTelemetry.ExtendedTraces;

// Sends the spans that the app's code makes during a run to Inngest, which shows them in the run's trace: the
// TypeScript SDK's InngestSpanProcessor. The call's span (inngest.execution) is declared as the root of a run, and every
// span that starts under a tracked span is tracked too, whatever its source: HTTP clients, databases, model calls. They
// are tagged with what Inngest needs to place them, the trace reference from the trace state Inngest sent with the
// call, the run, and the step whose handler made them, and sent over OTLP/HTTP to /v1/traces/userland at the end of the
// call. Spans that never end, such as those of a function abandoned at a new step, are dropped with the call.
//
// The listener samples the spans it tracks, so they are recorded even where the app's own sampler would drop them; it
// lets every other span alone. It also asks ASP.NET Core and the Connect worker for the span of the request, without
// recording it, so that the call's span inherits the trace context Inngest sent.
internal sealed partial class ExtendedTraceExporter : IAsyncDisposable
{
    public const string Path = "/v1/traces/userland";

    // What one export request carries, and how many ended spans wait for the next export before new ones are dropped.
    private const int BatchSize = 512;
    private const int MaxQueued = 8192;

    // An export that takes longer than this is given up, as the OTLP exporters do by default.
    private static readonly TimeSpan ExportTimeout = TimeSpan.FromSeconds(10);

    private readonly ConcurrentDictionary<ActivitySpanId, Tracked> _tracked = new();
    private readonly ConcurrentQueue<Activity> _ended = new();
    private readonly SemaphoreSlim _exporting = new(1, 1);
    private readonly InngestApiTransport _transport;
    private readonly ILogger _logger;
    private readonly ActivityListener _listener;
    private readonly IReadOnlyList<KeyValuePair<string, object?>> _resource = ExtendedTraceResource.Attributes;
    private int _queued;
    private int _warned;

    public ExtendedTraceExporter(IHttpClientFactory httpClientFactory, InngestSettings settings, ILogger<ExtendedTraceExporter> logger)
    {
        _transport = new InngestApiTransport(httpClientFactory, settings, logger);
        _logger = logger;
        _listener = new ActivityListener
        {
            ShouldListenTo = static _ => true,
            Sample = Sample,
            ActivityStarted = OnStarted,
            ActivityStopped = OnStopped,
        };
        ActivitySource.AddActivityListener(_listener);
    }

    // Tracks the call's span as the root of the run's spans. A call without Inngest's trace reference, which the server
    // needs to place a span, is not tracked.
    public void DeclareRoot(Activity call, string runId)
    {
        if (call.TraceStateString is not { Length: > 0 } traceState
            || TraceStateValue(traceState, "inngest@traceref=") is not { } traceRef)
        {
            return;
        }

        var run = new Run(
            runId,
            TraceParentOf(call),
            traceRef,
            TraceStateValue(traceState, "inngest@app="),
            TraceStateValue(traceState, "inngest@fn="),
            call.SpanId);
        _tracked[call.SpanId] = new Tracked(run, Step: null);
        Tag(call, run, step: null, isRoot: true);
        foreach (var (key, value) in _resource)
        {
            call.SetTag(key, value);
        }
    }

    // Tracks the span of a step the app executes, and makes the spans under it the step's. The step's span is a child of
    // the call's span, so it gets the id of the executor's span for the step as the parent Inngest should give it.
    public void DeclareStep(Activity span, StepInfo step, int attempt)
    {
        if (!_tracked.TryGetValue(span.ParentSpanId, out var owner))
        {
            return;
        }

        var run = owner.Run;
        Interlocked.Increment(ref run.StepsDeclared);
        var context = new StepContext(step.UserlandId ?? step.Id, step.Index ?? 0, step.HashedId, attempt);
        _tracked[span.SpanId] = new Tracked(run, context);
        Tag(span, run, context, isRoot: false);
    }

    // Sends what has ended since the last export. Called at the end of every call, as the TypeScript SDK flushes at the
    // end of every request, so that a host that stops once it has answered loses nothing.
    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        if (_ended.IsEmpty)
        {
            return;
        }

        await _exporting.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            while (!_ended.IsEmpty)
            {
                var batch = new List<Activity>(Math.Min(BatchSize, Volatile.Read(ref _queued)));
                while (batch.Count < BatchSize && _ended.TryDequeue(out var span))
                {
                    Interlocked.Decrement(ref _queued);
                    batch.Add(span);
                }

                await ExportAsync(batch, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _exporting.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        _listener.Dispose();
        await FlushAsync().ConfigureAwait(false);
        _exporting.Dispose();
    }

    private ActivitySamplingResult Sample(ref ActivityCreationOptions<ActivityContext> options)
    {
        if (options.Parent != default && _tracked.ContainsKey(options.Parent.SpanId))
        {
            return ActivitySamplingResult.AllDataAndRecorded;
        }

        var source = options.Source.Name;
        if (source == InngestInstrumentation.ActivitySourceName)
        {
            return options.Name == InngestTelemetry.CallSpan ? ActivitySamplingResult.AllDataAndRecorded : ActivitySamplingResult.PropagationData;
        }

        return source == "Microsoft.AspNetCore" ? ActivitySamplingResult.PropagationData : ActivitySamplingResult.None;
    }

    private void OnStarted(Activity activity)
    {
        if (activity.ParentSpanId == default || !_tracked.TryGetValue(activity.ParentSpanId, out var owner))
        {
            return;
        }

        // Once a step has run in the call, spans outside a step are the SDK's own work between steps, such as a
        // checkpoint, and not the app's, as the TypeScript SDK decides for checkpointed runs.
        if (owner.Step is null && Volatile.Read(ref owner.Run.StepsDeclared) > 0)
        {
            return;
        }

        _tracked[activity.SpanId] = owner;
        Tag(activity, owner.Run, owner.Step, isRoot: false);
    }

    private void OnStopped(Activity activity)
    {
        if (!_tracked.TryRemove(activity.SpanId, out var tracked))
        {
            return;
        }

        // The spans of a call that are still open when its span ends belong to code that was abandoned, and never end.
        if (activity.SpanId == tracked.Run.RootSpanId)
        {
            foreach (var entry in _tracked)
            {
                if (ReferenceEquals(entry.Value.Run, tracked.Run))
                {
                    _tracked.TryRemove(entry.Key, out _);
                }
            }
        }

        if (Interlocked.Increment(ref _queued) > MaxQueued)
        {
            Interlocked.Decrement(ref _queued);
            WarnOnce("the queue of spans is full");
            return;
        }

        _ended.Enqueue(activity);
    }

    private static void Tag(Activity span, Run run, StepContext? step, bool isRoot)
    {
        if (step is not null)
        {
            span.SetTag(Attributes.StepId, step.Id);
            span.SetTag(Attributes.StepIndex, step.Index);
            span.SetTag(Attributes.StepHash, step.HashedId);
            span.SetTag(Attributes.StepAttempt, step.Attempt);

            // The direct children of the call's span are placed under the executor's span of the step, whose id the
            // executor derives from the step's hashed id.
            if (!isRoot && span.ParentSpanId == run.RootSpanId)
            {
                span.SetTag(Attributes.StepParentSpanId, DeterministicSpanId.Create(step.HashedId));
            }
        }

        span.SetTag(Attributes.TraceParent, run.TraceParent);
        span.SetTag(Attributes.RunId, run.RunId);
        if (run.AppId is not null)
        {
            span.SetTag(Attributes.SdkAppId, run.AppId);
            span.SetTag(Attributes.SysAppId, run.AppId);
        }

        if (run.FunctionId is not null)
        {
            span.SetTag(Attributes.FunctionId, run.FunctionId);
        }

        span.SetTag(Attributes.TraceRef, run.TraceRef);
    }

    private async Task ExportAsync(List<Activity> batch, CancellationToken cancellationToken)
    {
        if (batch.Count == 0)
        {
            return;
        }

        using var body = new PooledBufferWriter();
        OtlpJson.Write(body, _resource, batch);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ExportTimeout);
        try
        {
            using var response = await _transport
                .SendAsync(new ApiCall(HttpMethod.Post, _transport.Url(Path), body.WrittenMemory), HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                WarnOnce(string.Create(CultureInfo.InvariantCulture, $"Inngest answered {(int)response.StatusCode} ({response.StatusCode})"));
            }
        }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            WarnOnce(exception.Message);
        }
    }

    // A failed export drops its spans, as the TypeScript SDK does, and says so once, so that a server that refuses them
    // does not fill the log.
    private void WarnOnce(string reason)
    {
        if (Interlocked.Exchange(ref _warned, 1) == 0)
        {
            Log.ExportFailed(_logger, reason);
        }
    }

    // Inngest sends the traceparent of the executor's span with every call. The call's span continues that trace, under
    // the request's span when the host has one; its topmost local ancestor has the executor's span as its parent.
    private static string TraceParentOf(Activity call)
    {
        var top = call;
        while (top.Parent is { } parent)
        {
            top = parent;
        }

        var flags = (top.ActivityTraceFlags & ActivityTraceFlags.Recorded) != 0 ? "01" : "00";
        return $"00-{top.TraceId.ToHexString()}-{top.ParentSpanId.ToHexString()}-{flags}";
    }

    // A W3C trace state is a comma-separated list of key=value entries.
    private static string? TraceStateValue(string traceState, string prefix)
    {
        foreach (var entry in traceState.Split(','))
        {
            var trimmed = entry.Trim();
            if (trimmed.StartsWith(prefix, StringComparison.Ordinal))
            {
                return trimmed[prefix.Length..];
            }
        }

        return null;
    }

    // The run a tracked span belongs to, and the step whose handler made it, if any.
    private sealed record Tracked(Run Run, StepContext? Step);

    private sealed class Run(string runId, string traceParent, string traceRef, string? appId, string? functionId, ActivitySpanId rootSpanId)
    {
        public int StepsDeclared;

        public string RunId { get; } = runId;

        public string TraceParent { get; } = traceParent;

        public string TraceRef { get; } = traceRef;

        public string? AppId { get; } = appId;

        public string? FunctionId { get; } = functionId;

        public ActivitySpanId RootSpanId { get; } = rootSpanId;
    }

    private sealed record StepContext(string Id, int Index, string HashedId, int Attempt);

    // The names Inngest reads, from the TypeScript SDK's otel/consts.ts.
    private static class Attributes
    {
        public const string TraceParent = "inngest.traceparent";
        public const string TraceRef = "inngest.traceref";
        public const string RunId = "sdk.run.id";
        public const string SdkAppId = "sdk.app.id";
        public const string SysAppId = "sys.app.id";
        public const string FunctionId = "sys.function.id";
        public const string StepParentSpanId = "inngest.step.parentSpanId";
        public const string StepId = "inngest.step.id";
        public const string StepIndex = "inngest.step.index";
        public const string StepHash = "inngest.step.hash";
        public const string StepAttempt = "inngest.step.attempt";
    }

    private static partial class Log
    {
        [LoggerMessage(1, LogLevel.Warning, "Failed to send extended traces to Inngest, so spans were dropped: {Reason}. Later failures are not logged.")]
        public static partial void ExportFailed(ILogger logger, string reason);
    }
}
