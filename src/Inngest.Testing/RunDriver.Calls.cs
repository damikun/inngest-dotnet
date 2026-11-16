// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using System.Buffers;
using System.Text.Json;
using Inngest.Errors;
using Inngest.Execution;
using Inngest.Internal;
using Inngest.Json;

namespace Inngest.Testing;

// Calling the function the way Inngest does, and turning the end of the run into a TestRun.
internal sealed partial class RunDriver
{
    private static JsonElement Parse(ReadOnlySpan<byte> json)
    {
        var reader = new Utf8JsonReader(json);
        return JsonElement.ParseValue(ref reader);
    }

    // What Inngest stores for a step that ran: {"data": <output>} or {"error": <error>}, with the value as the SDK wrote it.
    private byte[] Wrap(string property, JsonElement value)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = WireWriter.Create(buffer, _host.SerializerOptions))
        {
            writer.WriteStartObject();
            writer.WritePropertyName(property);
            if (value.ValueKind == JsonValueKind.Undefined)
            {
                writer.WriteNullValue();
            }
            else
            {
                // Copied rather than written again, which would fail on a lone surrogate.
#if NET9_0_OR_GREATER
                writer.WriteRawValue(System.Runtime.InteropServices.JsonMarshal.GetRawUtf8Value(value), skipInputValidation: true);
#else
                writer.WriteRawValue(value.GetRawText(), skipInputValidation: true);
#endif
            }

            writer.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }

    private async Task<Answer> CallAsync(string? stepId, int attempt, string? jobId, CancellationToken cancellationToken)
    {
        if (++_calls > MaxCalls)
        {
            throw new InvalidOperationException(
                $"Function \"{_function.Id}\" was called {MaxCalls} times without finishing. The test host stops a run that keeps adding steps.");
        }

        _runError = null;
        _stepErrors.Clear();
        _mocked.Clear();

        using var body = Canonical(attempt);

        var scope = _host.CreateScope();
        await using (scope.ConfigureAwait(false))
        {
            Calling.Value = this;
            var request = new ExecutionRequest
            {
                Function = _function,
                Body = body.WrittenMemory,
                StepId = stepId ?? "step",
                Services = scope.ServiceProvider,
                RequestId = Ulid.New(_clock.Now),
                JobId = jobId,
            };

            using var result = await _host.Engine.ExecuteAsync(request, cancellationToken).ConfigureAwait(false);
            return new Answer(result.StatusCode, result.Body.WrittenSpan.ToArray(), result.NoRetry);
        }
    }

    // The call body in the canonical form Inngest sends: the keys of every object sorted, and every number gone through
    // a double, so that events and step results read the way they do in production. Events, and invoke results that are
    // objects, have no lone surrogates left, as they are written the way Inngest decodes them. Inngest keeps other step
    // data as the SDK wrote it, and its canonicalizer refuses a lone surrogate there unless another \u escape follows
    // it, and then turns the two into one U+FFFD. The test host leaves a body with a lone surrogate as it was written.
    private PooledBufferWriter Canonical(int attempt)
    {
        using var written = new PooledBufferWriter();
        WriteBody(written, attempt);
        var body = new PooledBufferWriter(written.WrittenCount);
        try
        {
            try
            {
                var options = new JsonReaderOptions { MaxDepth = JsonPayload.DocumentOptions(_host.SerializerOptions).MaxDepth };
                JsonCanonicalizer.Write(written.WrittenSpan, body, options);
            }
            catch (InvalidOperationException)
            {
                body.Clear();
                body.Write(written.WrittenSpan);
            }

            return body;
        }
        catch
        {
            body.Dispose();
            throw;
        }
    }

    // A call body as Inngest sends it: keys sorted, and the stack listing results in the order they completed.
    private void WriteBody(IBufferWriter<byte> body, int attempt)
    {
        using var writer = new Utf8JsonWriter(body);
        writer.WriteStartObject();
        writer.WriteStartObject("ctx");
        writer.WriteNumber("attempt", attempt);
        writer.WriteBoolean("disable_immediate_execution", _forceStepPlan);
        writer.WriteNumber("max_attempts", _maxAttempts);
        writer.WriteString("run_id", RunId);
        writer.WriteStartObject("stack");
        writer.WriteNumber("current", _stack.Count);
        writer.WriteStartArray("stack");
        foreach (var hashedId in _stack)
        {
            writer.WriteStringValue(hashedId);
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
        writer.WriteBoolean("use_api", false);
        writer.WriteEndObject();

        writer.WritePropertyName("event");
        writer.WriteRawValue(_events[0], skipInputValidation: true);
        writer.WriteStartArray("events");
        foreach (var @event in _events)
        {
            writer.WriteRawValue(@event, skipInputValidation: true);
        }

        writer.WriteEndArray();
        writer.WriteStartObject("steps");
        foreach (var (hashedId, memo) in _memo)
        {
            writer.WritePropertyName(hashedId);
            writer.WriteRawValue(memo, skipInputValidation: true);
        }

        writer.WriteEndObject();
        writer.WriteNumber("version", 2);
        writer.WriteEndObject();
    }

    private TestRun Fail(Exception error, byte[]? errorJson = null)
    {
        if (errorJson is null)
        {
            var buffer = new ArrayBufferWriter<byte>();
            using (var writer = WireWriter.Create(buffer, _host.SerializerOptions))
            {
                ErrorSerializer.Write(writer, error);
            }

            errorJson = buffer.WrittenSpan.ToArray();
        }

        return Finish(TestRunStatus.Failed, error: error, errorJson: errorJson);
    }

    // The SDK failed before the function could throw, for example on an event that does not fit the function's
    // event type, so only the error Inngest would record is left.
    private static InngestException Rebuild(byte[] body)
    {
        using var document = JsonDocument.Parse(body);
        var error = ErrorSerializer.Read(document.RootElement);
        return new InngestException($"{error.Name}: {error.Message}");
    }

    private TestRun Finish(TestRunStatus status, JsonElement? output = null, Exception? error = null, byte[]? errorJson = null)
    {
        var options = _host.SerializerOptions;
        var steps = new TestStep[_found.Count];
        var waitingOn = new List<TestStep>();
        for (var i = 0; i < steps.Length; i++)
        {
            steps[i] = _found[i].ToTestStep(options);
            if (status == TestRunStatus.Waiting && _pending.Contains(_found[i].HashedId))
            {
                waitingOn.Add(steps[i]);
            }
        }

        TestStep[] stoppedAt = status == TestRunStatus.Stopped && _stoppedAt is { } checkpoint
            ? [.. checkpoint.Select(step => steps[_found.IndexOf(step)])]
            : [];
        return new TestRun(RunId, _function.Id, status, output, error, steps, waitingOn, [.. _sent], [.. _signals], [.. _published], options)
        {
            ErrorJson = errorJson,
            StoppedAt = stoppedAt,
        };
    }
}
