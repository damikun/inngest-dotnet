// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using System.Text.Json.Serialization;

namespace Inngest;

/// <summary>
/// What became of one item of a fan-out: the result of its step, or the error of the step that failed for good. It is
/// also what the function of a chunk returns for each item, which is why it serializes to JSON, leaving out a default
/// value and a missing error.
/// </summary>
/// <param name="Index">The position of the item in the list.</param>
/// <param name="Value">The result of the item's step; default when it failed.</param>
/// <param name="Error">The error the item's step failed with, after all its attempts; null when it succeeded.</param>
public readonly record struct FanOutOutcome<TResult>(
    int Index,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] TResult? Value,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] FunctionError? Error)
{
    /// <summary>Whether the item's step produced a result.</summary>
    [JsonIgnore]
    public bool Succeeded => Error is null;
}
