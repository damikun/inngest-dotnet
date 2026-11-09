// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using System.Diagnostics;

namespace Inngest.Execution;

// Gathers the AI metadata of one run of a step's handler from the activities that stop in its async flow, which is
// where Microsoft.Extensions.AI's OpenTelemetryChatClient and other GenAI instrumentation stop theirs. inngest-js does
// the same with a span processor that follows the spans under the step. Activities that stop after the handler has
// finished, in code it left running, are not counted.
internal sealed class AIMetadataCollector
{
    private static readonly AsyncLocal<AIMetadataCollector?> Current = new();

    // One listener for the process: a second would count every call twice.
    private static readonly ActivityListener Listener = Listen();

    private readonly object _gate = new();
    private AIMetadata? _metadata;

    // Activities around those already counted, such as the orchestrate_tools activity of FunctionInvokingChatClient or
    // an agent's invoke_agent, which repeat the usage of the calls inside them.
    private HashSet<Activity>? _enclosing;
    private bool _closed;

    // Starts collecting in the current async flow, which the handler then starts from. The caller restores the flow
    // with Exit once the handler has started, so the engine's own flow is not collected.
    public void Enter()
    {
        GC.KeepAlive(Listener);
        Current.Value = this;
    }

    public static void Exit() => Current.Value = null;

    // What the handler's calls added up to, or null when it made none.
    public AIMetadata? Close()
    {
        lock (_gate)
        {
            _closed = true;
            return _metadata;
        }
    }

    private void Add(Activity activity)
    {
        if (AIMetadata.From(activity) is not { } metadata)
        {
            return;
        }

        lock (_gate)
        {
            if (_closed || _enclosing?.Contains(activity) == true)
            {
                return;
            }

            if (_metadata is null)
            {
                _metadata = metadata;
            }
            else
            {
                _metadata.Add(metadata);
            }

            _enclosing ??= new HashSet<Activity>(ReferenceEqualityComparer.Instance);
            for (var parent = activity.Parent; parent is not null; parent = parent.Parent)
            {
                _enclosing.Add(parent);
            }
        }
    }

    private static ActivityListener Listen()
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = static _ => true,
            Sample = static (ref ActivityCreationOptions<ActivityContext> options) => Sample(options.Source, options.Parent.TraceFlags),
            SampleUsingParentId = static (ref ActivityCreationOptions<string> options) => Sample(options.Source, ActivityTraceFlags.None),
            ActivityStopped = static activity => Current.Value?.Add(activity),
        };

        ActivitySource.AddActivityListener(listener);
        return listener;
    }

    // Microsoft.Extensions.AI sets the gen_ai attributes only on an activity some listener wants all data of, so in a
    // step its activities are asked for here, whether or not the app's OpenTelemetry listens to them. They stay
    // recorded as their parent is, so the app's sampling of what runs inside them does not change. Other sources are
    // read only when the app samples them itself.
    private static ActivitySamplingResult Sample(ActivitySource source, ActivityTraceFlags parentFlags)
    {
        if (Current.Value is not { _closed: false } || !IsChatClientSource(source.Name))
        {
            return ActivitySamplingResult.None;
        }

        return (parentFlags & ActivityTraceFlags.Recorded) != 0 ? ActivitySamplingResult.AllDataAndRecorded : ActivitySamplingResult.AllData;
    }

    // The default source of OpenTelemetryChatClient, and the name it may take once it leaves "Experimental".
    private static bool IsChatClientSource(string name) =>
        name.Equals("Experimental.Microsoft.Extensions.AI", StringComparison.Ordinal)
        || name.Equals("Microsoft.Extensions.AI", StringComparison.Ordinal);
}
