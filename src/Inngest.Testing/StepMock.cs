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

    public virtual bool CanAnswer(StepType type) => type == Answers;
}

// Also answers the steps that run in the SDK like a run step: an AI wrap and a sent signal, whose answer is the id
// of the run it resumed.
internal sealed class RunMock(Func<int, object?> answer) : StepMock
{
    public override StepType Answers => StepType.Run;

    public override bool CanAnswer(StepType type) => type is StepType.Run or StepType.AIWrap or StepType.SendSignal;

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

    public byte[] ToMemo(JsonSerializerOptions options) => Memo.Write(options, writer => _write(writer, options));
}

// Writes what Inngest memoizes when a signal arrives: {"signal": <name>, "data": <data>}. A mock without data
// times the wait out.
internal sealed class SignalMock(Action<Utf8JsonWriter, JsonSerializerOptions>? writeData) : StepMock
{
    public override StepType Answers => StepType.WaitForSignal;

    public bool TimesOut => writeData is null;

    public byte[] ToMemo(string signal, JsonSerializerOptions options) => Memo.Write(options, writer =>
    {
        writer.WriteStartObject();
        writer.WriteString("signal", signal);
        writer.WritePropertyName("data");
        writeData!(writer, options);
        writer.WriteEndObject();
    });
}

// Writes what Inngest memoizes for a request it made itself, {"data": <result>}: the response of a fetch step, or
// the provider's response to an inference step.
internal sealed class GatewayMock(StepType answers, Action<Utf8JsonWriter, JsonSerializerOptions> write) : StepMock
{
    public override StepType Answers => answers;

    public byte[] ToMemo(JsonSerializerOptions options) => Memo.Write(options, writer =>
    {
        writer.WriteStartObject();
        writer.WritePropertyName("data");
        write(writer, options);
        writer.WriteEndObject();
    });
}

internal static class Memo
{
    public static byte[] Write(JsonSerializerOptions options, Action<Utf8JsonWriter> write)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = WireWriter.Create(buffer, options))
        {
            write(writer);
        }

        return buffer.WrittenSpan.ToArray();
    }
}
