// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using System.Text.Json;

namespace Inngest;

/// <summary>
/// Implemented by middleware that rewrites the JSON passing between Inngest and the function, before it is read into
/// .NET types and after they are written, which is what encryption and serialization of one's own need. JSON coming
/// in reaches the transforms in registration order and JSON going out in reverse order, so the first registered
/// middleware is the outermost layer both ways. Each method either writes one replacement value to the writer and
/// returns true, or writes nothing and returns false to keep the value; implement only the ones you need, the others
/// keep every value. Calls whose middleware does not implement this interface never serialize anything twice.
/// </summary>
public interface IPayloadTransform
{
    /// <summary>Rewrites a triggering event before the function's event type is read from it, which happens before the function's context exists.</summary>
    bool TransformEvent(JsonElement @event, Utf8JsonWriter writer) => false;

    /// <summary>Rewrites a memoized result of any step type, such as the event a wait matched, before the step's result type is read from it.</summary>
    bool TransformStepResult(FunctionContext context, StepInfo step, JsonElement result, Utf8JsonWriter writer) => false;

    /// <summary>Rewrites what a step handler returned before Inngest memoizes it.</summary>
    bool TransformStepOutput(FunctionContext context, StepInfo step, JsonElement output, Utf8JsonWriter writer) => false;

    /// <summary>Rewrites the function's output before it is sent to Inngest.</summary>
    bool TransformFunctionOutput(FunctionContext context, JsonElement output, Utf8JsonWriter writer) => false;
}
