// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using System.Collections;
using Microsoft.Extensions.Logging;

namespace Inngest.Execution;

// The logger a function sees. Every request replays the function from the top, so messages are dropped until
// memoization ends; code before that point already logged in an earlier request. Each message carries the run
// id and event name in a scope.
internal sealed class ReplaySafeLogger(ILogger inner, string runId, string eventName) : ILogger
{
    private readonly RunScope _scope = new(runId, eventName);
    private volatile bool _enabled;

    public void Enable() => _enabled = true;

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull =>
        inner.BeginScope(state);

    public bool IsEnabled(LogLevel logLevel) => _enabled && inner.IsEnabled(logLevel);

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel))
        {
            return;
        }

        using (inner.BeginScope(_scope))
        {
            inner.Log(logLevel, eventId, state, exception, formatter);
        }
    }

    // A list of key/value pairs, so that structured logging providers record runID and eventName as properties.
    private sealed class RunScope(string runId, string eventName) : IReadOnlyList<KeyValuePair<string, object?>>
    {
        private string? _text;

        public int Count => 2;

        public KeyValuePair<string, object?> this[int index] => index switch
        {
            0 => new KeyValuePair<string, object?>("runID", runId),
            1 => new KeyValuePair<string, object?>("eventName", eventName),
            _ => throw new ArgumentOutOfRangeException(nameof(index)),
        };

        public IEnumerator<KeyValuePair<string, object?>> GetEnumerator()
        {
            yield return this[0];
            yield return this[1];
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        public override string ToString() => _text ??= $"runID:{runId} eventName:{eventName}";
    }
}
