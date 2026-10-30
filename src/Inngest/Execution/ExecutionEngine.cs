// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using System.Collections.Concurrent;
using System.Text.Json;
using Inngest.Client;
using Inngest.Configuration;
using Inngest.Errors;
using Inngest.Internal;
using Microsoft.Extensions.Logging;

namespace Inngest.Execution;

// Runs every function in pure async mode with execution version 2: each call replays the function, then either
// executes one step, reports the steps it found, or answers with the function's output or error.
internal sealed class ExecutionEngine : IExecutionEngine
{
    private readonly ConcurrentDictionary<string, ILogger> _functionLoggers = new(StringComparer.Ordinal);
    private readonly ILoggerFactory _loggerFactory;
    private readonly IInngestApi _api;

    public ExecutionEngine(
        InngestSettings settings,
        ILoggerFactory loggerFactory,
        TimeProvider timeProvider,
        IInngestApi api,
        ICheckpointApi? checkpoints = null)
    {
        Settings = settings;
        TimeProvider = timeProvider;
        Logger = loggerFactory.CreateLogger<ExecutionEngine>();
        Checkpoints = checkpoints;
        _loggerFactory = loggerFactory;
        _api = api;
    }

    public InngestSettings Settings { get; }

    public TimeProvider TimeProvider { get; }

    public ILogger Logger { get; }

    // Where calls under checkpointing save their steps. Without it every call answers after one step.
    public ICheckpointApi? Checkpoints { get; }

    public async Task<ExecutionResult> ExecuteAsync(ExecutionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(request.Body);
        }
        catch (JsonException exception)
        {
            return InternalError(exception);
        }

        JsonDocument? steps = null;
        JsonDocument? events = null;
        try
        {
            RunRequest run;
            try
            {
                run = RunRequest.Parse(document.RootElement);
            }
            catch (JsonException exception)
            {
                return InternalError(exception);
            }

            if (run.UseApi)
            {
                try
                {
                    (steps, events) = await LoadRunStateAsync(run.RunId, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
                {
                    Log.RunStateUnavailable(Logger, run.RunId, exception);
                    return InternalError(exception);
                }

                run.LoadSteps(steps.RootElement);
                run.LoadEvents(events.RootElement);
            }

            Invocation invocation;
            try
            {
                invocation = new Invocation(this, request, run, cancellationToken);
            }
            catch (JsonException exception)
            {
                // The event does not fit the function's event type. Retrying the same payload cannot help.
                Log.InvocationFailed(Logger, run.RunId, request.Function.Id, exception);
                return FinalError(exception);
            }
            catch (Exception exception)
            {
                Log.InvocationFailed(Logger, run.RunId, request.Function.Id, exception);
                return InternalError(exception);
            }

            return await invocation.RunAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            document.Dispose();
            steps?.Dispose();
            events?.Dispose();
        }
    }

    // Loggers are cached because LoggerFactory takes a lock to look one up.
    public ILogger GetFunctionLogger(string functionId) =>
        _functionLoggers.GetOrAdd(functionId, static (id, factory) => factory.CreateLogger("Inngest.Function." + id), _loggerFactory);

    // Inngest leaves the state out of calls when it is too large and the SDK loads it from the API instead.
    private async Task<(JsonDocument Steps, JsonDocument Events)> LoadRunStateAsync(string runId, CancellationToken cancellationToken)
    {
        var steps = _api.GetRunStepsAsync(runId, cancellationToken);
        var events = _api.GetRunEventsAsync(runId, cancellationToken);
        try
        {
            await Task.WhenAll(steps, events).ConfigureAwait(false);
            if (steps.Result.RootElement.ValueKind != JsonValueKind.Object || events.Result.RootElement.ValueKind != JsonValueKind.Array)
            {
                throw new JsonException($"The Inngest API returned run state of an unexpected shape for run {runId}.");
            }

            return (steps.Result, events.Result);
        }
        catch
        {
            // Either request may have succeeded on its own; its document must not leak.
            if (steps.IsCompletedSuccessfully)
            {
                steps.Result.Dispose();
            }

            if (events.IsCompletedSuccessfully)
            {
                events.Result.Dispose();
            }

            throw;
        }
    }

    // A function error that no retry can fix, reported the way a NonRetriableException would be.
    private ExecutionResult FinalError(Exception exception)
    {
        var body = new PooledBufferWriter();
        using (var writer = WireWriter.Create(body, Settings.SerializerOptions))
        {
            ErrorSerializer.Write(writer, exception);
        }

        return new ExecutionResult(400, body) { NoRetry = true };
    }

    // A failure of the SDK rather than of the function. Inngest retries it like any other error.
    private ExecutionResult InternalError(Exception exception)
    {
        var body = new PooledBufferWriter();
        using (var writer = WireWriter.Create(body, Settings.SerializerOptions))
        {
            ErrorSerializer.WriteInternal(writer, exception);
        }

        return new ExecutionResult(500, body) { NoRetry = false };
    }
}
