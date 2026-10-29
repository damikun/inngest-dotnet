// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Inngest.Testing;

public enum TestRunStatus
{
    // The function returned; Output holds what it returned.
    Completed,

    // The function failed for good; Error holds what it failed with.
    Failed,

    // The run waits on steps the test did not mock, or that the test host does not support; see WaitingOn.
    Waiting,

    // ExecuteStepAsync reached its step and stopped the run there.
    Stopped,
}

// A run as the test host left it: its outcome, every step it reached and the events it sent.
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

    // The function id as registered, without the app prefix.
    public string FunctionId { get; }

    public TestRunStatus Status { get; }

    // What the function returned, as the JSON Inngest stores; null unless the run completed.
    public JsonElement? Output { get; }

    // The exception the run failed with, as the function or the SDK threw it; null unless the run failed.
    public Exception? Error { get; }

    // Every step the run reached, in the order Inngest heard of them.
    public IReadOnlyList<TestStep> Steps { get; }

    // The steps a waiting run waits on; empty unless the run is waiting.
    public IReadOnlyList<TestStep> WaitingOn { get; }

    // Every event the run sent, from send steps and from the client, in the order it sent them.
    public IReadOnlyList<InngestEvent> SentEvents { get; }

    // The run of the function's failure handler, which the host starts when the function fails for good, as
    // Inngest does.
    public TestRun? FailureHandler { get; internal set; }

    // The error as Inngest records it, which a failure handler receives.
    internal ReadOnlyMemory<byte> ErrorJson { get; init; }

    // Reads the output with the app's serializer options, the way a function invoking this one would.
    public T? GetOutput<T>()
    {
        if (Output is not { } output)
        {
            throw new InvalidOperationException($"The run has no output. {this}");
        }

        return output.Deserialize((JsonTypeInfo<T>)_serializerOptions.GetTypeInfo(typeof(T)));
    }

    // Finds a step by the id the code passed, with ":<n>" for a repeated id.
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
