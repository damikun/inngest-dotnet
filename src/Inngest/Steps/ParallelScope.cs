// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

namespace Inngest.Steps;

// The parallel mode of the IStepTools.ParallelAsync callback the code runs in. It is async-local, so it flows into
// the awaits and Task.Run calls of the callback and ends with it, and steps read it where they are created.
internal static class ParallelScope
{
    private static readonly AsyncLocal<ParallelMode> Current = new();

    public static ParallelMode Mode => Current.Value;

    public static Task<T> Run<T>(ParallelMode mode, Func<Task<T>> steps)
    {
        ArgumentNullException.ThrowIfNull(steps);
        var outer = Current.Value;
        Current.Value = mode;
        try
        {
            return steps();
        }
        catch (Exception exception)
        {
            return Task.FromException<T>(exception);
        }
        finally
        {
            Current.Value = outer;
        }
    }
}
