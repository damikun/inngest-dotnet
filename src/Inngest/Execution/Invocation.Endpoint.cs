// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using System.Text;
using System.Text.Json;
using Inngest.Errors;
using Inngest.Internal;
using Inngest.Middleware;

namespace Inngest.Execution;

// The ways out of a durable endpoint request that starts a run. Its steps ran in this call and wait in the checkpoint
// buffer. A step that has to wait, planned steps and a failure Inngest retries hand the run to Inngest, which creates it
// now. An endpoint that finished, or failed for good, has its run created after the response instead.
internal sealed partial class Invocation
{
    // The id inngest-js gives the op that ends the run, and the error of the endpoint itself.
    private static readonly string CompleteId = StepIds.Hash("complete");

    private static readonly JsonEncodedText IdProperty = JsonEncodedText.Encode("id");
    private static readonly JsonEncodedText OpProperty = JsonEncodedText.Encode("op");

    // A step failed. As in inngest-js the failure goes to Inngest, which retries the step or, after its last attempt,
    // calls the endpoint again with the failure, which the endpoint then reads as a StepFailedException.
    private async Task<ExecutionResult> HandOverStepErrorAsync(FoundStep step, StepOutcome outcome)
    {
        var error = outcome.Error!;
        var (retriable, _) = Classify(error, step: true);
        ReportStepError(step, error, retriable);
        if (_pipeline is not null)
        {
            await AwaitHookAsync(_pipeline.OnStepErrorAsync(outcome.Info!, error, isFinalAttempt: !retriable)).ConfigureAwait(false);
        }

        using (var op = new PooledBufferWriter())
        {
            using (var writer = WireWriter.Create(op, _settings.SerializerOptions))
            {
                step.WriteOpStart(writer, retriable ? Ops.StepError : Ops.StepFailed, _settings);
                writer.WritePropertyName(ErrorProperty);
                ErrorSerializer.Write(writer, error);
                outcome.Timing.Write(writer);
                writer.WriteEndObject();
            }

            _checkpoint!.AddOp(op.WrittenSpan);
        }

        return await HandOverAsync().ConfigureAwait(false);
    }

    // Steps the endpoint found that do not run in this call, such as a sleep or steps in parallel.
    private async ValueTask<ExecutionResult> HandOverStepsAsync(List<FoundStep> steps)
    {
        var ops = new List<PooledBufferWriter>();
        try
        {
            foreach (var step in steps)
            {
                if (!step.IsNew)
                {
                    continue;
                }

                var op = new PooledBufferWriter();
                ops.Add(op);
                using var writer = WireWriter.Create(op, _settings.SerializerOptions);
                step.WriteOpStart(writer, step.Op, _settings);
                writer.WriteEndObject();
            }

            // Buffered only once all of them could be written.
            foreach (var op in ops)
            {
                _checkpoint!.AddOp(op.WrittenSpan);
            }
        }
        catch (Exception exception)
        {
            // Only user data can fail here, such as an invoke payload that cannot be serialized.
            return await RejectAsync(exception).ConfigureAwait(false);
        }
        finally
        {
            foreach (var op in ops)
            {
                op.Dispose();
            }
        }

        return await HandOverAsync().ConfigureAwait(false);
    }

    // The endpoint threw. While attempts remain, Inngest retries it from its last saved step, as inngest-js has it.
    private async ValueTask<ExecutionResult> RejectEndpointAsync(Exception exception)
    {
        var (retriable, _) = Classify(exception, step: false);
        ReportRunError(exception, retriable);
        if (_pipeline is not null)
        {
            await AwaitHookAsync(_pipeline.OnRunErrorAsync(exception, isFinalAttempt: !retriable)).ConfigureAwait(false);
        }

        if (!retriable)
        {
            return FailEndpoint(exception);
        }

        using (var op = new PooledBufferWriter())
        {
            using (var writer = WireWriter.Create(op, _settings.SerializerOptions))
            {
                writer.WriteStartObject();
                writer.WriteString(IdProperty, CompleteId);
                writer.WriteString(OpProperty, Ops.StepError);
                writer.WritePropertyName(ErrorProperty);
                ErrorSerializer.Write(writer, exception);
                writer.WriteEndObject();
            }

            _checkpoint!.AddOp(op.WrittenSpan);
        }

        return await HandOverAsync().ConfigureAwait(false);
    }

    // Saves the buffered ops, which creates the run, and leaves the rest of it to Inngest.
    private async Task<ExecutionResult> HandOverAsync()
    {
        try
        {
            await _checkpoint!.SaveAsync(_cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (!_cancellationToken.IsCancellationRequested)
        {
            Log.EndpointHandOverFailed(_logger, _run.RunId, _functionId, exception);
            return ExecutionResult.ForEndpoint(EndpointOutcome.Failed(_run.RunId, exception));
        }

        if (_endpoint!.Run?.Token is not { Length: > 0 } token)
        {
            var missing = new InngestException($"Inngest took run {_run.RunId} over but sent no token to wait for its output with.");
            Log.EndpointHandOverFailed(_logger, _run.RunId, _functionId, missing);
            return ExecutionResult.ForEndpoint(EndpointOutcome.Failed(_run.RunId, missing));
        }

        Log.EndpointHandedOver(_logger, _run.RunId, _functionId);
        return ExecutionResult.ForEndpoint(EndpointOutcome.HandedOver(_run.RunId, token));
    }

    // The endpoint finished; its output is its response. A request that ran no steps leaves no run.
    private async ValueTask<ExecutionResult> CompleteEndpointAsync(object? output)
    {
        if (_pipeline is not null)
        {
            await AwaitHookAsync(_pipeline.OnRunCompleteAsync(output)).ConfigureAwait(false);
        }

        if (_checkpoint!.Count == 0)
        {
            return ExecutionResult.ForEndpoint(EndpointOutcome.Completed(_run.RunId, output, finish: null));
        }

        try
        {
            AddRunComplete(writer => PayloadTransforms.WriteFunctionOutput(writer, output, _settings.SerializerOptions, transforms: null));
        }
        catch (Exception exception)
        {
            return FailEndpoint(exception);
        }

        return ExecutionResult.ForEndpoint(EndpointOutcome.Completed(_run.RunId, output, DeferSave()));
    }

    // The endpoint failed for good. A request that ran steps records its run as failed, with a 500 and the error.
    private ExecutionResult FailEndpoint(Exception exception)
    {
        if (_checkpoint!.Count == 0)
        {
            return ExecutionResult.ForEndpoint(EndpointOutcome.Failed(_run.RunId, exception));
        }

        AddRunComplete(writer =>
        {
            writer.WriteStartObject();
            writer.WriteNumber("status"u8, 500);
            writer.WriteStartObject("headers"u8);
            writer.WriteString("content-type"u8, "application/json");
            writer.WriteEndObject();
            writer.WriteString("body"u8, SerializeError(exception));
            writer.WriteEndObject();
        });
        return ExecutionResult.ForEndpoint(EndpointOutcome.Failed(_run.RunId, exception, DeferSave()));
    }

    private void AddRunComplete(Action<Utf8JsonWriter> writeData)
    {
        using var op = new PooledBufferWriter();
        using (var writer = WireWriter.Create(op, _settings.SerializerOptions))
        {
            writer.WriteStartObject();
            writer.WriteString(IdProperty, CompleteId);
            writer.WriteString(OpProperty, Ops.RunComplete);
            writer.WritePropertyName(DataProperty);
            writeData(writer);
            writer.WriteEndObject();
        }

        _checkpoint!.AddOp(op.WrittenSpan);
    }

    private string SerializeError(Exception exception)
    {
        using var body = new PooledBufferWriter();
        using (var writer = WireWriter.Create(body, _settings.SerializerOptions))
        {
            ErrorSerializer.Write(writer, exception);
        }

        return Encoding.UTF8.GetString(body.WrittenSpan);
    }

    // Takes the buffered ops out of the call, which has answered by the time they are sent. A failure is only logged:
    // the client has its response, and the run is just not recorded.
    private Func<CancellationToken, Task> DeferSave()
    {
        var steps = _checkpoint!.TakeSteps();
        var saver = _endpoint!;
        var logger = _logger;
        var runId = _run.RunId;
        var functionId = _functionId;
        return async cancellationToken =>
        {
            try
            {
                await saver.SaveAsync(steps, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                Log.EndpointRunNotRecorded(logger, runId, functionId, exception);
            }
        };
    }
}
