// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using System.Collections;

namespace Inngest.Execution;

// The logging scopes InngestOptions.LogScopes opens: one around every call from Inngest and one around every step
// handler. They are lists of key/value pairs, so that structured logging providers such as Serilog, Seq and
// Application Insights record each as a property, and they use the property names of ReplaySafeLogger's scope.
internal static class LogScopes
{
    public const string FunctionId = "functionID";
    public const string RunId = "runID";
    public const string Attempt = "attempt";
    public const string EventName = "eventName";
    public const string StepId = "stepID";
}

internal sealed class CallScope(string functionId, string runId, int attempt, string eventName) : IReadOnlyList<KeyValuePair<string, object?>>
{
    private object? _attempt;
    private string? _text;

    public int Count => 4;

    public KeyValuePair<string, object?> this[int index] => index switch
    {
        0 => new KeyValuePair<string, object?>(LogScopes.FunctionId, functionId),
        1 => new KeyValuePair<string, object?>(LogScopes.RunId, runId),

        // Boxed once, however many providers read the scope.
        2 => new KeyValuePair<string, object?>(LogScopes.Attempt, _attempt ??= attempt),
        3 => new KeyValuePair<string, object?>(LogScopes.EventName, eventName),
        _ => throw new ArgumentOutOfRangeException(nameof(index)),
    };

    public IEnumerator<KeyValuePair<string, object?>> GetEnumerator()
    {
        for (var i = 0; i < Count; i++)
        {
            yield return this[i];
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public override string ToString() =>
        _text ??= $"{LogScopes.FunctionId}:{functionId} {LogScopes.RunId}:{runId} {LogScopes.Attempt}:{attempt} {LogScopes.EventName}:{eventName}";
}

internal sealed class StepScope(string stepId) : IReadOnlyList<KeyValuePair<string, object?>>
{
    public int Count => 1;

    public KeyValuePair<string, object?> this[int index] => index == 0
        ? new KeyValuePair<string, object?>(LogScopes.StepId, stepId)
        : throw new ArgumentOutOfRangeException(nameof(index));

    public IEnumerator<KeyValuePair<string, object?>> GetEnumerator()
    {
        yield return this[0];
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public override string ToString() => $"{LogScopes.StepId}:{stepId}";
}
