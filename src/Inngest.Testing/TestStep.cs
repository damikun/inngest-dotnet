// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Inngest.Testing;

/// <summary>A step of a test run, as Inngest recorded it.</summary>
public sealed class TestStep
{
    private readonly JsonSerializerOptions _serializerOptions;

    internal TestStep(
        string id,
        string hashedId,
        StepType? type,
        string op,
        JsonElement options,
        TestStepStatus status,
        int attempts,
        bool mocked,
        JsonElement? output,
        Exception? error,
        IReadOnlyList<Exception> errors,
        JsonSerializerOptions serializerOptions)
    {
        Id = id;
        HashedId = hashedId;
        Type = type;
        Op = op;
        Options = options;
        Status = status;
        Attempts = attempts;
        Mocked = mocked;
        Output = output;
        Error = error;
        Errors = errors;
        _serializerOptions = serializerOptions;
    }

    /// <summary>The id the code passed, with ":&lt;n&gt;" for a repeated id.</summary>
    public string Id { get; }

    /// <summary>What Inngest stores the step under.</summary>
    public string HashedId { get; }

    /// <summary>Null for a step the test host does not know, such as one added by a newer SDK; Op names it.</summary>
    public StepType? Type { get; }

    /// <summary>
    /// The last operation the SDK sent Inngest for the step, such as StepRun, StepFailed, Sleep or WaitForEvent.
    /// </summary>
    public string Op { get; }

    /// <summary>
    /// The operation's opts as sent, such as the event and timeout of a wait or the duration of a sleep.
    /// </summary>
    public JsonElement Options { get; }

    public TestStepStatus Status { get; }

    /// <summary>
    /// How many times the step's handler, or its mock, ran: once per attempt. Zero for a sleep, wait or invoke,
    /// which Inngest carries out.
    /// </summary>
    public int Attempts { get; }

    /// <summary>Whether a mock answered the step instead of its handler or Inngest.</summary>
    public bool Mocked { get; }

    /// <summary>
    /// The result the function receives, as the JSON Inngest stores: the value of a run step or an invoke, the
    /// event a wait received or null when it timed out, null for a sleep. Null unless the step completed.
    /// </summary>
    public JsonElement? Output { get; }

    /// <summary>What the step failed with for good; null unless it failed.</summary>
    public Exception? Error { get; }

    /// <summary>What each failed attempt threw, in order, including attempts that a retry made up for.</summary>
    public IReadOnlyList<Exception> Errors { get; }

    /// <summary>Reads the output with the app's serializer options, as the function does on replay.</summary>
    public T? GetOutput<T>()
    {
        if (Output is not { } output)
        {
            throw new InvalidOperationException($"Step \"{Id}\" has no output: it is {Status}.");
        }

        return output.Deserialize((JsonTypeInfo<T>)_serializerOptions.GetTypeInfo(typeof(T)));
    }

    public override string ToString() => $"{Describe()}: {Status} after {Attempts} attempt{(Attempts == 1 ? string.Empty : "s")}";

    internal string Describe() => ReportedOp.IsSupported(Type)
        ? $"step \"{Id}\" ({Op})"
        : $"step \"{Id}\" ({Op}, which the test host does not support)";
}
