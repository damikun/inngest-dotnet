// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using System.Buffers;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Inngest.Errors;
using Inngest.Execution;

namespace Inngest.Testing;

// How the host answers one step. Its type has to match the step the code calls.
internal abstract class StepMock
{
    public abstract StepType Answers { get; }
}

internal sealed class RunMock(Func<int, object?> answer) : StepMock
{
    public override StepType Answers => StepType.Run;

    public object? Answer(int attempt) => answer(attempt);
}

internal sealed class SleepMock : StepMock
{
    public static readonly SleepMock Instance = new();

    public override StepType Answers => StepType.Sleep;
}

// Received is null when the wait times out.
internal sealed class WaitMock(InngestEvent? received) : StepMock
{
    public override StepType Answers => StepType.WaitForEvent;

    public InngestEvent? Received { get; } = received;
}

// Writes what Inngest memoizes when an invoked function finishes: {"data": <output>} or {"error": <error>}.
internal sealed class InvokeMock : StepMock
{
    private readonly Action<Utf8JsonWriter, JsonSerializerOptions> _write;

    private InvokeMock(Exception? error, Type? function, Action<Utf8JsonWriter, JsonSerializerOptions> write)
    {
        Error = error;
        Function = function;
        _write = write;
    }

    public override StepType Answers => StepType.Invoke;

    public Exception? Error { get; }

    // The function class the test says the step invokes, or null when it named none.
    public Type? Function { get; }

    public static InvokeMock Returning<T>(T result, Type? function = null) => new(null, function, (writer, options) =>
    {
        writer.WriteStartObject();
        writer.WritePropertyName("data");
        JsonSerializer.Serialize(writer, result, (JsonTypeInfo<T>)options.GetTypeInfo(typeof(T)));
        writer.WriteEndObject();
    });

    public static InvokeMock Throwing(Exception error) => new(error, null, (writer, _) =>
    {
        writer.WriteStartObject();
        writer.WritePropertyName("error");
        ErrorSerializer.Write(writer, error);
        writer.WriteEndObject();
    });

    public byte[] ToMemo(JsonSerializerOptions options)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = WireWriter.Create(buffer, options))
        {
            _write(writer, options);
        }

        return buffer.WrittenSpan.ToArray();
    }
}
