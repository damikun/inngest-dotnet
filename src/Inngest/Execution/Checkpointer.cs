// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using System.Text.Json;
using Inngest.Client;
using Inngest.Configuration;
using Inngest.Internal;
using Inngest.Json;

namespace Inngest.Execution;

// Checkpointing for one call: the steps it ran that Inngest has not saved yet, when to save them, and how long the
// call may go on running steps. Only the invocation's logical thread uses it.
internal sealed class Checkpointer : IDisposable
{
    public static readonly TimeSpan ServeMaxRuntime = TimeSpan.FromSeconds(10);

    // Connect has no HTTP request timeout to stay under.
    public static readonly TimeSpan ConnectMaxRuntime = TimeSpan.FromMinutes(5);

    // Where the buffered ops go, as a JSON array. Null for a durable endpoint, which takes its ops and sends them itself.
    private readonly Func<ReadOnlyMemory<byte>, CancellationToken, Task>? _save;
    private readonly TimeProvider _time;
    private readonly int _bufferedSteps;
    private readonly TimeSpan _maxRuntime;
    private readonly TimeSpan? _maxInterval;
    private readonly long _started;

    // A step's output sits one level down in its op, so the op is read with room for whatever the serializer wrote.
    private readonly JsonReaderOptions _readerOptions;

    // Where each buffered op ends in the array.
    private readonly List<int> _ends = [];

    // The buffered ops as a JSON array, without its closing bracket until it is sent.
    private PooledBufferWriter? _steps;
    private bool _closed;
    private long _oldest;

    private Checkpointer(
        Func<ReadOnlyMemory<byte>, CancellationToken, Task>? save,
        TimeProvider time,
        int bufferedSteps,
        TimeSpan maxRuntime,
        TimeSpan? maxInterval,
        JsonSerializerOptions serializerOptions)
    {
        _save = save;
        _time = time;
        _bufferedSteps = bufferedSteps;
        _maxRuntime = maxRuntime;
        _maxInterval = maxInterval;
        _started = time.GetTimestamp();
        _readerOptions = new JsonReaderOptions { MaxDepth = JsonPayload.DocumentOptions(serializerOptions).MaxDepth };
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
    // queue item and lets steps run right away. The function's options win over the app's. receivedAt is when the call
    // arrived, before its state was loaded.
    public static Checkpointer? Create(
        ICheckpointApi? api,
        InngestSettings settings,
        ExecutionRequest request,
        RunRequest run,
        DateTimeOffset receivedAt,
        TimeProvider time)
    {
        if (api is null
            || (request.Function.Options.Checkpointing ?? settings.Checkpointing) is not { Enabled: true } options
            || run.DisableImmediateExecution
            || run.InternalFunctionId is not { } functionId
            || run.QueueItemId is not { } queueItemId)
        {
            return null;
        }

        var call = new AsyncCheckpoint(run.RunId, functionId, queueItemId, request.RequestId, request.GenerationId, receivedAt, default);
        return new Checkpointer(
            (steps, cancellationToken) => api.CheckpointAsync(call with { Steps = steps }, cancellationToken),
            time,
            options.BufferedSteps,
            options.MaxRuntime ?? request.DefaultCheckpointMaxRuntime,
            options.MaxInterval,
            settings.SerializerOptions);
    }

    // A durable endpoint request keeps its steps until the run is handed to Inngest or the endpoint is done, and then
    // takes them out to send them; see EndpointSaver.
    public static Checkpointer ForEndpoint(TimeProvider time, JsonSerializerOptions serializerOptions) =>
        new(save: null, time, int.MaxValue, Timeout.InfiniteTimeSpan, maxInterval: null, serializerOptions);

    // Buffers a StepRun op and returns its data as a later call reads it from Inngest: canonicalized, with sorted keys
    // and numbers that went through a double. So the function sees the same value now as on every replay, and a step
    // id built from it stays the same. The copy outlives the buffer, which is reused once the op is sent.
    public JsonElement Add(ReadOnlySpan<byte> op)
    {
        var reader = new Utf8JsonReader(op, _readerOptions);
        using var document = JsonDocument.ParseValue(ref reader);
        var data = JsonCanonicalizer.Canonicalize(document.RootElement.GetProperty("data"), _readerOptions);
        AddOp(op);
        return data;
    }

    // Buffers any op, such as the planned steps and errors a durable endpoint hands over.
    public void AddOp(ReadOnlySpan<byte> op)
    {
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
        _ends.Add(steps.WrittenCount);
        Count++;
    }

    // Saves the buffered steps. Throws InngestException when Inngest did not save them; they stay buffered then.
    public async Task SaveAsync(CancellationToken cancellationToken)
    {
        await _save!(Close(), cancellationToken).ConfigureAwait(false);
        Discard();
    }

    // The buffered ops of a durable endpoint, which outlive the call.
    public EndpointOps TakeOps()
    {
        if (Count == 0)
        {
            return EndpointOps.Empty;
        }

        var ops = new EndpointOps(Close().ToArray(), [.. _ends]);
        Discard();
        return ops;
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
        _ends.Clear();
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
