// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Inngest.Errors;
using Inngest.Internal;
using Inngest.Middleware;

namespace Inngest.Execution;

// Writing the answer to Inngest: step results and errors, the steps found, and the output or failure of the function.
internal sealed partial class Invocation
{
    // Returns the exception when the output cannot be serialized, which then fails the step.
    private Exception? WriteStepRun(PooledBufferWriter body, FoundStep step, object? output, Timing timing)
    {
        try
        {
            using var writer = WireWriter.Create(body, _settings.SerializerOptions);
            writer.WriteStartArray();
            WriteStepRunOp(writer, step, output, timing);
            writer.WriteEndArray();
            return null;
        }
        catch (Exception exception)
        {
            body.Clear();
            return exception;
        }
    }

    // The one place a StepRun op is written, for the classic answer and the checkpoint alike, so the payload
    // transforms of the call's middleware apply to both.
    private void WriteStepRunOp(Utf8JsonWriter writer, FoundStep step, object? output, Timing timing)
    {
        step.WriteOpStart(writer, Ops.StepRun, _settings);
        writer.WritePropertyName(DataProperty);
        step.WriteData(writer, output, _settings.SerializerOptions);
        timing.Write(writer);
        writer.WriteEndObject();
    }

    private void WriteStepError(PooledBufferWriter body, FoundStep step, Exception error, bool final, Timing timing)
    {
        using var writer = WireWriter.Create(body, _settings.SerializerOptions);
        writer.WriteStartArray();
        step.WriteOpStart(writer, final ? Ops.StepFailed : Ops.StepError, _settings);
        writer.WritePropertyName(ErrorProperty);
        ErrorSerializer.Write(writer, error);
        timing.Write(writer);
        writer.WriteEndObject();
        writer.WriteEndArray();
    }

    private async ValueTask<ExecutionResult> ReportAsync(List<FoundStep> steps)
    {
        await EndMemoizationAsync().ConfigureAwait(false);
        var body = new PooledBufferWriter();
        try
        {
            using (var writer = WireWriter.Create(body, _settings.SerializerOptions))
            {
                writer.WriteStartArray();
                foreach (var step in steps)
                {
                    if (step.IsNew)
                    {
                        step.WriteOpStart(writer, step.Op, _settings);
                        writer.WriteEndObject();
                    }
                }

                writer.WriteEndArray();
            }

            return new ExecutionResult(206, body);
        }
        catch (Exception exception)
        {
            // Only user data can fail here, such as an invoke payload that cannot be serialized.
            body.Dispose();
            return await RejectAsync(exception).ConfigureAwait(false);
        }
    }

    private async ValueTask<ExecutionResult> CompleteAsync(Task<object?> function)
    {
        if (!function.IsCompletedSuccessfully)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            var error = function.IsFaulted ? function.Exception!.InnerException! : CancellationOf(function);
            return await RejectAsync(error).ConfigureAwait(false);
        }

        var output = function.Result;

        // Steps the function started but never awaited still have to run. They are reported instead of the
        // output, which the function returns again on a later call, and a requested step that is one of them
        // runs now even though the function did not wait for it.
        var requested = _target is null ? null : FindTarget(_found);
        if (requested is not null || (_target is null && _found.Exists(static step => step.IsNew)))
        {
            if (_pipeline is not null)
            {
                await AwaitHookAsync(_pipeline.OnRunCompleteAsync(output)).ConfigureAwait(false);
            }

            return requested is null
                ? await ReportAsync(_found).ConfigureAwait(false)
                : await ExecuteAsync(requested).ConfigureAwait(false);
        }

        var body = new PooledBufferWriter();
        try
        {
            using var writer = WireWriter.Create(body, _settings.SerializerOptions);
            PayloadTransforms.WriteFunctionOutput(writer, output, _settings.SerializerOptions, Transforms);
        }
        catch (Exception exception)
        {
            body.Dispose();
            return await RejectAsync(exception).ConfigureAwait(false);
        }

        if (_pipeline is not null)
        {
            try
            {
                await AwaitHookAsync(_pipeline.OnRunCompleteAsync(output)).ConfigureAwait(false);
            }
            catch
            {
                body.Dispose();
                throw;
            }
        }

        return new ExecutionResult(200, body);
    }

    private async ValueTask<ExecutionResult> RejectAsync(Exception exception)
    {
        var (retriable, retryAfter) = Classify(exception, step: false);
        ReportRunError(exception, retriable);
        if (_pipeline is not null)
        {
            await AwaitHookAsync(_pipeline.OnRunErrorAsync(exception, isFinalAttempt: !retriable)).ConfigureAwait(false);
        }

        var body = new PooledBufferWriter();
        using (var writer = WireWriter.Create(body, _settings.SerializerOptions))
        {
            ErrorSerializer.Write(writer, exception);
        }

        return new ExecutionResult(retriable ? 500 : 400, body) { NoRetry = !retriable, RetryAfter = retryAfter };
    }

    // The engine answers Inngest instead of letting user exceptions reach the host, so it reports them itself: in
    // the log, and on the call's activity for tracing.
    private void ReportRunError(Exception exception, bool retriable)
    {
        if (retriable)
        {
            Log.RunErrored(_runLogger, _run.RunId, _functionId, _run.Attempt, exception);
        }
        else
        {
            Log.RunFailed(_runLogger, _run.RunId, _functionId, _run.Attempt, exception);
        }

        RecordOnActivity(exception);
    }

    private void ReportStepError(FoundStep step, Exception exception, bool retriable)
    {
        if (retriable)
        {
            Log.StepErrored(_runLogger, step.FinalId, _run.RunId, _functionId, _run.Attempt, exception);
        }
        else
        {
            Log.StepFailed(_runLogger, step.FinalId, _run.RunId, _functionId, _run.Attempt, exception);
        }

        RecordOnActivity(exception);
    }

    private static void RecordOnActivity(Exception exception)
    {
        if (Activity.Current is { } activity)
        {
            activity.AddException(exception);
            activity.SetStatus(ActivityStatusCode.Error, exception.Message);
        }
    }

    private (bool Retriable, string? RetryAfter) Classify(Exception exception, bool step)
    {
        if (_run.MaxAttempts is { } maxAttempts && maxAttempts > 0 && _run.Attempt >= maxAttempts - 1)
        {
            return (false, null);
        }

        return exception switch
        {
            NonRetriableException => (false, null),

            // A step failure the function did not catch fails the run without retrying it.
            StepFailedException when !step => (false, null),
            RetryAfterException retryAfter => (true, FormatRetryAfter(retryAfter)),
            _ => (true, null),
        };
    }

    // Inngest caps retry delays at a day and reads only short numbers as seconds, so longer delays are capped here.
    private static string FormatRetryAfter(RetryAfterException exception) =>
        exception.RetryAt is { } retryAt
            ? TimeString.Format(retryAt)
            : ((long)Math.Ceiling(Math.Min(exception.Delay.GetValueOrDefault().TotalSeconds, MaxRetryDelay.TotalSeconds)))
                .ToString(CultureInfo.InvariantCulture);

    // A canceled task rethrows the exception that canceled it, which keeps the message and stack of, say, an
    // HttpClient timeout.
    private static Exception CancellationOf(Task task)
    {
        try
        {
            task.GetAwaiter().GetResult();
        }
        catch (OperationCanceledException exception)
        {
            return exception;
        }

        return new TaskCanceledException(task);
    }

    private async ValueTask<ExecutionResult> StepNotFoundAsync()
    {
        await EndMemoizationAsync().ConfigureAwait(false);
        Log.StepNotFound(_logger, _run.RunId, _functionId, _target!, StepNotFoundTimeout);

        var found = _found.FindAll(static step => step.IsNew);
        found.Sort(static (a, b) => string.CompareOrdinal(a.HashedId, b.HashedId));
        var shown = Math.Min(found.Count, MaxStepsInNotFoundReport);

        // The same diagnostics the TypeScript SDK sends.
        var message = new StringBuilder();
        message.Append(CultureInfo.InvariantCulture, $"Could not find step \"{_target}\" to run; timed out.");
        if (shown > 0)
        {
            message.Append(" Found new steps: \n");
            for (var i = 0; i < shown; i++)
            {
                message.Append(CultureInfo.InvariantCulture, $"{(i > 0 ? "\n" : string.Empty)}{found[i].DisplayName} ({found[i].HashedId})");
            }

            message.Append('.');
        }

        if (found.Count > shown)
        {
            message.Append(CultureInfo.InvariantCulture, $" (showing {shown} of {found.Count})");
        }

        var body = new PooledBufferWriter();
        using (var writer = WireWriter.Create(body, _settings.SerializerOptions))
        {
            writer.WriteStartObject();
            writer.WriteString("error", message.ToString());
            writer.WriteString("requestedStep", _target);
            writer.WriteStartArray("foundSteps");
            for (var i = 0; i < shown; i++)
            {
                writer.WriteStartObject();
                writer.WriteString("id", found[i].HashedId);
                if (found[i].GetName() is { } name)
                {
                    writer.WriteString("name", name);
                }

                writer.WriteString("displayName", found[i].DisplayName);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteNumber("totalFoundSteps", found.Count);
            writer.WriteEndObject();
        }

        return new ExecutionResult(500, body) { NoRetry = false };
    }
}
