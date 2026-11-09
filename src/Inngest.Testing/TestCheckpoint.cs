// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

namespace Inngest.Testing;

/// <summary>
/// A point in a run where <see cref="InngestTestHost.RunUntilAsync(string, InngestEvent, TestCheckpoint, StepMocks, CancellationToken)"/>
/// stops it, as run.waitFor does in the TypeScript test package. The run then ends as Stopped, with the steps of the
/// checkpoint in <see cref="TestRun.StoppedAt"/>, unless it completed, failed or waited before reaching it.
/// </summary>
public sealed class TestCheckpoint
{
    private TestCheckpoint(CheckpointKind kind, IReadOnlyList<string> stepIds)
    {
        Kind = kind;
        StepIds = stepIds;
    }

    internal enum CheckpointKind
    {
        StepDone,
        StepRan,
        StepsFound,
    }

    internal CheckpointKind Kind { get; }

    internal IReadOnlyList<string> StepIds { get; }

    /// <summary>
    /// Stops once the function reports steps it has not run, before Inngest takes them on: the steps of a parallel
    /// group, which Inngest runs in calls of their own, or a sleep, wait or invoke. With ids, only once one report holds
    /// all of them, such as the whole group. The steps are pending, with their options as the function sent them.
    /// </summary>
    public static TestCheckpoint StepsFound(params string[] stepIds)
    {
        ArgumentNullException.ThrowIfNull(stepIds);
        foreach (var stepId in stepIds)
        {
            ArgumentException.ThrowIfNullOrEmpty(stepId, nameof(stepIds));
        }

        return new TestCheckpoint(CheckpointKind.StepsFound, [.. stepIds]);
    }

    /// <summary>
    /// Stops once the handler of the step, or its mock, has run an attempt, whether it returned or threw. A step that
    /// threw is still pending, with the exception in its Errors, and the run has not retried it yet.
    /// </summary>
    public static TestCheckpoint StepRan(string stepId)
    {
        ArgumentException.ThrowIfNullOrEmpty(stepId);
        return new TestCheckpoint(CheckpointKind.StepRan, [stepId]);
    }

    // What ExecuteStepAsync stops at: the step has run for good, or Inngest has it.
    internal static TestCheckpoint StepDone(string stepId) => new(CheckpointKind.StepDone, [stepId]);

    public override string ToString() => Kind switch
    {
        CheckpointKind.StepsFound when StepIds.Count == 0 => "steps found",
        CheckpointKind.StepsFound => $"steps {string.Join(", ", StepIds.Select(static id => $"\"{id}\""))} found",
        CheckpointKind.StepRan => $"step \"{StepIds[0]}\" ran",
        _ => $"step \"{StepIds[0]}\" done",
    };
}
