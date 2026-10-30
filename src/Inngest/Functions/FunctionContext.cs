// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using System.Runtime.CompilerServices;
using System.Text.Json;
using Inngest.Events;
using Microsoft.Extensions.Logging;

namespace Inngest;

/// <summary>The run-level part of a function context, which middleware sees without knowing the event type.</summary>
public abstract class FunctionContext
{
    private readonly JsonElement _eventSource;
    private StrongBox<JsonElement>? _rawEvent;

    private protected FunctionContext()
    {
    }

    /// <summary>The function id as registered, without the app prefix.</summary>
    public required string FunctionId { get; init; }

    public required string RunId { get; init; }

    /// <summary>Zero for the first attempt of the current step or function, incremented on every retry.</summary>
    public required int Attempt { get; init; }

    public int? MaxAttempts { get; init; }

    public required IStepTools Step { get; init; }

    /// <summary>
    /// Suppresses messages while the run replays memoized steps, so each message is written once per run.
    /// </summary>
    public required ILogger Logger { get; init; }

    /// <summary>
    /// Scoped to this invocation, which is one call from Inngest. The function class and the middleware come from it, so
    /// a scoped service injected into them is shared by everything the call runs, and the call replays the function's own
    /// code. Step.RunScopedAsync gives a step a scope of its own.
    /// </summary>
    public required IServiceProvider Services { get; init; }

    /// <summary>
    /// Canceled when Inngest abandons the call or the app shuts down. Middleware uses it; functions also get it as a
    /// parameter.
    /// </summary>
    public CancellationToken CancellationToken { get; init; }

    /// <summary>
    /// Identifies this one call from Inngest; every call of a run has its own. Null when Inngest sent none.
    /// </summary>
    public string? RequestId { get; init; }

    /// <summary>The queue job this call belongs to. Null when Inngest sent none.</summary>
    public string? JobId { get; init; }

    /// <summary>
    /// The run's sessions: those every triggering event belongs to, at most five, ordered by key. Events the run
    /// sends and functions it invokes inherit them unless InngestOptions.SessionPropagation is off; empty then.
    /// </summary>
    public IReadOnlyDictionary<string, string> Sessions { get; init; } = EventSessions.None;

    /// <summary>
    /// The triggering event, or the first event of a batch, for code that does not know the event type.
    /// </summary>
    public abstract string EventName { get; }

    public abstract string? EventId { get; }

    /// <summary>
    /// The triggering event as Inngest sent it. It is copied out of the request on first use, so the value stays
    /// valid after the call; read it during the call. Undefined when the context was not created by the engine.
    /// </summary>
    public JsonElement RawEvent
    {
        get
        {
            if (Volatile.Read(ref _rawEvent) is { } cached)
            {
                return cached.Value;
            }

            if (_eventSource.ValueKind == JsonValueKind.Undefined)
            {
                return default;
            }

            var copy = new StrongBox<JsonElement>(_eventSource.Clone());
            return (Interlocked.CompareExchange(ref _rawEvent, copy, null) ?? copy).Value;
        }
    }

    // The event element of the request, which lives only as long as the call.
    internal JsonElement EventSource
    {
        init => _eventSource = value;
    }

    // The middleware of the call, the function's own included, which also wraps the events its steps send. Null
    // when the context was not created by the engine.
    internal IReadOnlyList<InngestMiddleware>? Middleware { get; init; }
}

public sealed class FunctionContext<TEventData> : FunctionContext
{
    public required InngestEvent<TEventData> Event { get; init; }

    /// <summary>All events of a batch when the function uses BatchEvents, otherwise just Event.</summary>
    public required IReadOnlyList<InngestEvent<TEventData>> Events { get; init; }

    public override string EventName => Event.Name;

    public override string? EventId => Event.Id;
}
