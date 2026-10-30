// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Inngest.Testing;

/// <summary>A run as the test host left it: its outcome, every step it reached and the events it sent.</summary>
public sealed class TestRun
{
    private readonly JsonSerializerOptions _serializerOptions;

    internal TestRun(
        string runId,
        string functionId,
        TestRunStatus status,
        JsonElement? output,
        Exception? error,
        IReadOnlyList<TestStep> steps,
        IReadOnlyList<TestStep> waitingOn,
        IReadOnlyList<InngestEvent> sentEvents,
        JsonSerializerOptions serializerOptions)
    {
        RunId = runId;
        FunctionId = functionId;
        Status = status;
        Output = output;
        Error = error;
        Steps = steps;
        WaitingOn = waitingOn;
        SentEvents = sentEvents;
        _serializerOptions = serializerOptions;
    }

    public string RunId { get; }

    /// <summary>The function id as registered, without the app prefix.</summary>
    public string FunctionId { get; }

    public TestRunStatus Status { get; }

    /// <summary>What the function returned, as the JSON Inngest stores; null unless the run completed.</summary>
    public JsonElement? Output { get; }

    /// <summary>
    /// The exception the run failed with, as the function or the SDK threw it; null unless the run failed.
    /// </summary>
    public Exception? Error { get; }

    /// <summary>Every step the run reached, in the order Inngest heard of them.</summary>
    public IReadOnlyList<TestStep> Steps { get; }

    /// <summary>The steps a waiting run waits on; empty unless the run is waiting.</summary>
    public IReadOnlyList<TestStep> WaitingOn { get; }

    /// <summary>Every event the run sent, from send steps and from the client, in the order it sent them.</summary>
    public IReadOnlyList<InngestEvent> SentEvents { get; }

    /// <summary>
    /// The run of the function's failure handler, which the host starts when the function fails for good, as
    /// Inngest does.
    /// </summary>
    public TestRun? FailureHandler { get; internal set; }

    // The error as Inngest records it, which a failure handler receives.
    internal ReadOnlyMemory<byte> ErrorJson { get; init; }

    /// <summary>
    /// Reads the output with the app's serializer options, the way a function invoking this one would.
    /// </summary>
    public T? GetOutput<T>()
    {
        if (Output is not { } output)
        {
            throw new InvalidOperationException($"The run has no output. {this}");
        }

        return output.Deserialize((JsonTypeInfo<T>)_serializerOptions.GetTypeInfo(typeof(T)));
    }

    /// <summary>Finds a step by the id the code passed, with ":&lt;n&gt;" for a repeated id.</summary>
    public TestStep GetStep(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        foreach (var step in Steps)
        {
            if (step.Id == id)
            {
                return step;
            }
        }

        var reached = Steps.Count == 0 ? "none" : string.Join(", ", Steps.Select(static step => $"\"{step.Id}\""));
        throw new KeyNotFoundException($"The run did not reach a step \"{id}\". Steps it reached: {reached}.");
    }

    public override string ToString() => Status switch
    {
        TestRunStatus.Completed => $"Completed with {Output?.GetRawText()}.",
        TestRunStatus.Failed => $"Failed with {Error?.GetType().Name}: {Error?.Message}",
        TestRunStatus.Waiting => $"Waiting on {string.Join(", ", WaitingOn.Select(static step => step.Describe()))}.",
        _ => Steps.Count == 0 ? "Stopped." : $"Stopped after {Steps[^1].Describe()}.",
    };
}
