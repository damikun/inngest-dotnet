// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using System.Collections.Concurrent;
using System.Net;
using System.Threading.Channels;
using Inngest.Internal;
using Inngest.Signing;
using Microsoft.Extensions.Logging;

namespace Inngest.Connect;

// Replies from the moment they are sent until the gateway acknowledges them. A reply that is not acknowledged within
// five seconds, or that finds no connection to go out on, is posted to Inngest over HTTP instead, one at a time, by a
// single flush loop.
//
// A reply has one owner at a time: the pending table while it waits for its acknowledgement, whoever takes it out
// of the table after that, and the flush queue once it is there.
internal sealed class ReplyBuffer
{
    public static readonly TimeSpan AcknowledgeDeadline = TimeSpan.FromSeconds(5);

    private const int MaxFlushRounds = 5;

    private readonly ConcurrentDictionary<string, Reply> _pending = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<Reply> _unacknowledged = new();

    // Requests to flush coalesce: one waiting request covers everything buffered before the flush starts.
    private readonly Channel<bool> _flushRequests = Channel.CreateBounded<bool>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true });

    private readonly ConnectApi _api;
    private readonly Func<SigningKey?> _currentKey;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;

    public ReplyBuffer(ConnectApi api, Func<SigningKey?> currentKey, TimeProvider time, ILogger logger)
    {
        _api = api;
        _currentKey = currentKey;
        _time = time;
        _logger = logger;
    }

    // How many replies wait to be flushed over HTTP.
    public int BufferedCount => _unacknowledged.Count;

    public void AddPending(Reply reply)
    {
        // A reply to the same request that is still waiting is superseded by this one.
        while (!_pending.TryAdd(reply.RequestId, reply))
        {
            if (_pending.TryRemove(reply.RequestId, out var older))
            {
                older.Dispose();
            }
        }

        // Started only once the reply is in the table, so the deadline always finds it there.
        reply.SetDeadline(_time.CreateTimer(
            static state =>
            {
                var (buffer, reply) = ((ReplyBuffer, Reply))state!;
                if (buffer.TryTakePending(reply))
                {
                    Log.ReplyNotAcknowledged(buffer._logger, reply.RequestId, AcknowledgeDeadline);
                    buffer.Buffer(reply);
                }
            },
            (this, reply),
            AcknowledgeDeadline,
            Timeout.InfiniteTimeSpan));
    }

    public void Acknowledge(string requestId)
    {
        if (_pending.TryRemove(requestId, out var reply))
        {
            reply.Dispose();
        }
    }

    // For a reply that was given to a connection which then closed before sending it.
    public void Unsent(Reply reply)
    {
        if (TryTakePending(reply))
        {
            Buffer(reply);
        }
    }

    // Queues the reply for the flush loop, which owns it from now on.
    public void Buffer(Reply reply)
    {
        _unacknowledged.Enqueue(reply);
        RequestFlush();
    }

    // Replies still waiting for an acknowledgement when the last connection has closed can only go over HTTP.
    public void BufferPending()
    {
        foreach (var pair in _pending)
        {
            if (_pending.TryRemove(pair))
            {
                Buffer(pair.Value);
            }
        }
    }

    public void RequestFlush() => _flushRequests.Writer.TryWrite(true);

    // Flushes whenever asked to until Complete, then flushes once more and returns.
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (await _flushRequests.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                _flushRequests.Reader.TryRead(out _);
                await FlushAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The host stopped waiting; what is left is lost with the process.
        }
        finally
        {
            while (_unacknowledged.TryDequeue(out var reply))
            {
                Log.ReplyDropped(_logger, reply.RequestId);
                reply.Dispose();
            }
        }
    }

    public void Complete()
    {
        RequestFlush();
        _flushRequests.Writer.TryComplete();
    }

    private bool TryTakePending(Reply reply) => _pending.TryRemove(KeyValuePair.Create(reply.RequestId, reply));

    private async Task FlushAsync(CancellationToken cancellationToken)
    {
        for (var round = 1; !await TryFlushAllAsync(cancellationToken).ConfigureAwait(false); round++)
        {
            if (round == MaxFlushRounds)
            {
                // They stay queued for the next flush, which the next connection triggers.
                Log.FlushGaveUp(_logger, _unacknowledged.Count, MaxFlushRounds);
                return;
            }

            await Task.Delay(Backoff.AfterFailures(round), _time, cancellationToken).ConfigureAwait(false);
        }
    }

    // Returns false at the first reply that does not get through, which stays first in line for the next round.
    private async Task<bool> TryFlushAllAsync(CancellationToken cancellationToken)
    {
        while (_unacknowledged.TryPeek(out var reply))
        {
            string? failure;
            try
            {
                var status = await _api.FlushAsync(_currentKey(), reply.Bytes, cancellationToken).ConfigureAwait(false);
                failure = status == HttpStatusCode.OK ? null : $"Inngest answered {(int)status}";
            }
            catch (HttpRequestException exception)
            {
                failure = exception.Message;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                failure = "the request timed out";
            }

            if (failure is not null)
            {
                Log.FlushFailed(_logger, reply.RequestId, failure);
                return false;
            }

            _unacknowledged.TryDequeue(out _);
            Log.Flushed(_logger, reply.RequestId);
            reply.Dispose();
        }

        return true;
    }
}

// The encoded SDKResponse for one request, in a pooled buffer that lives until the reply is acknowledged or flushed.
internal sealed class Reply(string requestId, PooledBufferWriter bytes) : IDisposable
{
    private PooledBufferWriter? _bytes = bytes;
    private ITimer? _deadline;

    public string RequestId { get; } = requestId;

    public ReadOnlyMemory<byte> Bytes => (_bytes ?? throw new ObjectDisposedException(nameof(Reply))).WrittenMemory;

    public void SetDeadline(ITimer deadline) => Interlocked.Exchange(ref _deadline, deadline)?.Dispose();

    public void Dispose()
    {
        Interlocked.Exchange(ref _deadline, null)?.Dispose();
        Interlocked.Exchange(ref _bytes, null)?.Dispose();
    }
}
