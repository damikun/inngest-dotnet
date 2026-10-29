// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using System.Text.Json;
using Inngest.Client;
using Inngest.Internal;

namespace Inngest.Execution;

// Checkpointing for one call: the steps it ran that Inngest has not saved yet, when to save them, and how long the
// call may go on running steps. Only the invocation's logical thread uses it.
internal sealed class Checkpointer : IDisposable
{
    public static readonly TimeSpan ServeMaxRuntime = TimeSpan.FromSeconds(10);

    // Connect has no HTTP request timeout to stay under.
    public static readonly TimeSpan ConnectMaxRuntime = TimeSpan.FromMinutes(5);

    private readonly ICheckpointApi _api;
    private readonly TimeProvider _time;
    private readonly AsyncCheckpoint _call;
    private readonly int _bufferedSteps;
    private readonly TimeSpan _maxRuntime;
    private readonly TimeSpan? _maxInterval;
    private readonly long _started;

    // The buffered StepRun ops as a JSON array, without its closing bracket until it is sent.
    private PooledBufferWriter? _steps;
    private bool _closed;
    private long _oldest;

    private Checkpointer(ICheckpointApi api, TimeProvider time, AsyncCheckpoint call, CheckpointingOptions options, TimeSpan maxRuntime)
    {
        _api = api;
        _time = time;
        _call = call;
        _bufferedSteps = options.BufferedSteps;
        _maxRuntime = maxRuntime;
        _maxInterval = options.MaxInterval;
        _started = time.GetTimestamp();
    }

    public int Count { get; private set; }

    // Past MaxRuntime the call runs no more steps itself and leaves the next one to Inngest.
    public bool RuntimeExceeded => _maxRuntime != Timeout.InfiniteTimeSpan && _time.GetElapsedTime(_started) >= _maxRuntime;

    // The buffer is full, or its oldest step has waited MaxInterval.
    public bool IsDue => Count >= _bufferedSteps || TimeUntilDue == TimeSpan.Zero;

    // How much longer the oldest buffered step may wait; null when nothing waits on MaxInterval.
    public TimeSpan? TimeUntilDue
    {
        get
        {
            if (Count == 0 || _maxInterval is not { } interval)
            {
                return null;
            }

            var remaining = interval - _time.GetElapsedTime(_oldest);
            return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
        }
    }

    // As in inngest-js, only calls that discover steps checkpoint, and only when Inngest named the function and the
    // queue item and lets steps run right away. The function's options win over the app's.
    public static Checkpointer? Create(ICheckpointApi? api, InngestSettings settings, ExecutionRequest request, RunRequest run, TimeProvider time)
    {
        if (api is null
            || (request.Function.Options.Checkpointing ?? settings.Checkpointing) is not { Enabled: true } options
            || run.DisableImmediateExecution
            || run.InternalFunctionId is not { } functionId
            || run.QueueItemId is not { } queueItemId)
        {
            return null;
        }

        var call = new AsyncCheckpoint(run.RunId, functionId, queueItemId, request.RequestId, request.GenerationId, time.GetUtcNow(), default);
        return new Checkpointer(api, time, call, options, options.MaxRuntime ?? request.DefaultCheckpointMaxRuntime);
    }

    // Buffers a StepRun op and returns its data as a later call reads it from Inngest, so the function sees the same
    // value now as on every replay. The copy outlives the buffer, which is reused once the op is sent.
    public JsonElement Add(ReadOnlySpan<byte> op)
    {
        var reader = new Utf8JsonReader(op);
        var data = JsonElement.ParseValue(ref reader).GetProperty("data");

        var steps = _steps ??= new PooledBufferWriter(op.Length + 2);
        if (Count == 0)
        {
            steps.Write("["u8);
            _oldest = _time.GetTimestamp();
        }
        else
        {
            steps.Write(","u8);
        }

        steps.Write(op);
        Count++;
        return data;
    }

    // Saves the buffered steps. Throws InngestException when Inngest did not save them; they stay buffered then.
    public async Task SaveAsync(CancellationToken cancellationToken)
    {
        await _api.CheckpointAsync(_call with { Steps = Close() }, cancellationToken).ConfigureAwait(false);
        Discard();
    }

    // Takes the buffered steps as the answer to the call, for when Inngest could not save them: Inngest then saves
    // them from the answer and calls again.
    public ExecutionResult TakeAnswer()
    {
        var steps = Close();
        var body = new PooledBufferWriter(steps.Length);
        body.Write(steps.Span);
        Discard();
        return new ExecutionResult(206, body);
    }

    public void Discard()
    {
        _steps?.Clear();
        _closed = false;
        Count = 0;
    }

    public void Dispose() => _steps?.Dispose();

    private ReadOnlyMemory<byte> Close()
    {
        if (!_closed)
        {
            _steps!.Write("]"u8);
            _closed = true;
        }

        return _steps!.WrittenMemory;
    }
}
