// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using System.Text.Json;
using Inngest.Execution;
using Inngest.Internal;

namespace Inngest.Middleware;

// The middleware of one call that implements IPayloadTransform. Incoming JSON passes through the transforms in
// registration order and outgoing JSON in reverse. A call without transforms has no instance of this: the engine
// checks for null and reads and writes JSON as it always did.
internal sealed class PayloadTransforms(IPayloadTransform[] transforms, FunctionContext context, JsonSerializerOptions options)
{
    // Null when no middleware of the call transforms payloads.
    public static IPayloadTransform[]? Find(InngestMiddleware[] middleware)
    {
        List<IPayloadTransform>? found = null;
        foreach (var item in middleware)
        {
            if (item is IPayloadTransform transform)
            {
                (found ??= []).Add(transform);
            }
        }

        return found?.ToArray();
    }

    // Events are read while the function's context is created, so this runs without one.
    public static TransformedJson TransformEvent(IPayloadTransform[] transforms, JsonElement @event, JsonSerializerOptions options) =>
        Apply(
            transforms,
            reverse: false,
            @event,
            options,
            nameof(IPayloadTransform.TransformEvent),
            default(object),
            static (transform, _, value, writer) => transform.TransformEvent(value, writer));

    // Writes the function's output as Inngest stores it: serialized with the app's options, then rewritten when the
    // call has transforms. Everything that sends the output to Inngest writes it through this.
    public static void WriteFunctionOutput(Utf8JsonWriter writer, object? output, JsonSerializerOptions options, PayloadTransforms? transforms)
    {
        if (transforms is null)
        {
            Serialize(writer, output, options);
        }
        else
        {
            transforms.RewriteFunctionOutput(writer, output);
        }
    }

    public TransformedJson TransformStepResult(StepInfo step, JsonElement result) =>
        Apply(
            transforms,
            reverse: false,
            result,
            options,
            nameof(IPayloadTransform.TransformStepResult),
            (Context: context, Step: step),
            static (transform, state, value, writer) => transform.TransformStepResult(state.Context, state.Step, value, writer));

    public void WriteStepOutput(Utf8JsonWriter writer, FoundStep step, object? output) =>
        WriteRewritten(
            writer,
            output,
            step.WriteOutput,
            nameof(IPayloadTransform.TransformStepOutput),
            (Context: context, Step: step.CreateInfo(memoized: false)),
            static (transform, state, value, writer) => transform.TransformStepOutput(state.Context, state.Step, value, writer));

    private void RewriteFunctionOutput(Utf8JsonWriter writer, object? output) =>
        WriteRewritten(
            writer,
            output,
            Serialize,
            nameof(IPayloadTransform.TransformFunctionOutput),
            context,
            static (transform, context, value, writer) => transform.TransformFunctionOutput(context, value, writer));

    private static void Serialize(Utf8JsonWriter writer, object? output, JsonSerializerOptions options)
    {
        if (output is null)
        {
            writer.WriteNullValue();
        }
        else
        {
            JsonSerializer.Serialize(writer, output, options.GetTypeInfo(output.GetType()));
        }
    }

    // Each transform sees what the one before it wrote, and only the latest rewrite is kept alive.
    private static TransformedJson Apply<TState>(
        IPayloadTransform[] transforms,
        bool reverse,
        JsonElement value,
        JsonSerializerOptions options,
        string hook,
        TState state,
        Func<IPayloadTransform, TState, JsonElement, Utf8JsonWriter, bool> transform)
    {
        var current = new TransformedJson(value);
        PooledBufferWriter? buffer = null;
        Utf8JsonWriter? writer = null;
        try
        {
            for (var i = 0; i < transforms.Length; i++)
            {
                var item = transforms[reverse ? transforms.Length - 1 - i : i];
                buffer ??= new PooledBufferWriter();
                if (writer is null)
                {
                    writer = WireWriter.Create(buffer, options);
                }
                else
                {
                    writer.Reset(buffer);
                }

                var rewritten = transform(item, state, current.Value, writer);
                var written = writer.BytesCommitted + writer.BytesPending > 0;
                if (!rewritten)
                {
                    if (written)
                    {
                        throw new InvalidOperationException(
                            $"{item.GetType().Name}.{hook} wrote a value but returned false. Return true to use what it wrote.");
                    }

                    continue;
                }

                writer.Flush();
                if (!written || writer.CurrentDepth != 0)
                {
                    throw new InvalidOperationException($"{item.GetType().Name}.{hook} returned true without writing a complete JSON value.");
                }

                var document = JsonDocument.Parse(buffer.WrittenMemory);
                current.Dispose();
                current = new TransformedJson(document, buffer);
                buffer = null;
            }

            return current;
        }
        catch
        {
            current.Dispose();
            throw;
        }
        finally
        {
            writer?.Dispose();
            buffer?.Dispose();
        }
    }

    // Serializes the output into a pooled buffer, passes it through the transforms in reverse order and writes the
    // result. Output no transform rewrote is copied as it was serialized.
    private void WriteRewritten<TState>(
        Utf8JsonWriter writer,
        object? output,
        Action<Utf8JsonWriter, object?, JsonSerializerOptions> serialize,
        string hook,
        TState state,
        Func<IPayloadTransform, TState, JsonElement, Utf8JsonWriter, bool> transform)
    {
        using var original = new PooledBufferWriter();
        using (var originalWriter = WireWriter.Create(original, options))
        {
            serialize(originalWriter, output, options);
        }

        using var document = JsonDocument.Parse(original.WrittenMemory);
        using var result = Apply(transforms, reverse: true, document.RootElement, options, hook, state, transform);
        writer.WriteRawValue(result.IsRewritten ? result.RewrittenJson : original.WrittenSpan, skipInputValidation: true);
    }
}
