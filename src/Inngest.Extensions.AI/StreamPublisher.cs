// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.

using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace Inngest.Extensions.AI;

// Publishes the text of a streaming model call to a realtime topic while the model produces it, in one request, as
// IInngestRealtime.PublishStreamAsync does. The text goes through a channel so that the model call never waits for the
// publish and never fails with it: a progress display is not worth a second call to a paid model. The model call is
// what the step memoizes, so a replay publishes nothing.
//
// Inngest takes a stream of up to 1,000 chunks of up to 1 KB, and a model sends an update for every few characters, so the
// text is gathered into chunks, which go out when they are full or when the text has waited for a short interval. The
// interval grows once the stream is long, so that the chunks last. A text that still outgrows the limit is published up to
// it, and the rest is left out with a warning.
internal sealed class StreamPublisher
{
    public const int MaxChunkBytes = 1024;
    public const int MaxChunks = 1000;

    // After this many chunks, the text waits ten times as long for the interval to pass.
    private const int SlowAfterChunks = 500;

    private readonly object _gate = new();
    private readonly Channel<string> _chunks = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
    private readonly TimeProvider _time;
    private readonly TimeSpan _interval;
    private readonly string _stepId;
    private readonly RealtimeTopic _topic;
    private readonly ILogger _logger;
    private readonly Task _publishing;
    private string _pending = string.Empty;
    private int _sent;
    private bool _closed;
    private bool _timerArmed;
    private ITimer? _timer;

    public StreamPublisher(
        IInngestRealtime realtime,
        RealtimeTopic topic,
        string stepId,
        ILogger logger,
        TimeProvider time,
        TimeSpan interval,
        CancellationToken cancellationToken)
    {
        _topic = topic;
        _stepId = stepId;
        _logger = logger;
        _time = time;
        _interval = interval;
        _publishing = PublishAsync(realtime, cancellationToken);
    }

    // Called by the model's loop, one update after the other.
    public void Write(string text)
    {
        if (text.Length == 0)
        {
            return;
        }

        var tooLong = false;
        lock (_gate)
        {
            if (_closed)
            {
                return;
            }

            _pending += text;
            tooLong = Flush(all: false, final: false);
            if (!_closed && _pending.Length > 0)
            {
                Arm();
            }
        }

        if (tooLong)
        {
            Log.StreamTooLong(_logger, _stepId, MaxChunks, MaxChunkBytes, _topic.Name, _topic.Channel);
        }
    }

    // Ends the stream and waits until Inngest has taken it. Never throws. A stream whose model failed is ended the same way:
    // Inngest ends a request that stops early like one that finished, so aborting it would tell subscribers no more.
    public Task CompleteAsync()
    {
        var tooLong = false;
        lock (_gate)
        {
            if (!_closed)
            {
                tooLong = Flush(all: true, final: true);
                _closed = true;
                _chunks.Writer.TryComplete();
            }

            _timer?.Dispose();
        }

        if (tooLong)
        {
            Log.StreamTooLong(_logger, _stepId, MaxChunks, MaxChunkBytes, _topic.Name, _topic.Channel);
        }

        return _publishing;
    }

    // Moves the pending text into chunks: the full ones, or all of it, unless more is coming and a character waits for its
    // other half. Returns whether the limit was reached, which ends the stream there. Called with the lock held.
    private bool Flush(bool all, bool final)
    {
        var offset = 0;
        var limitReached = false;
        while (offset < _pending.Length)
        {
            var length = ChunkLength(_pending, offset, all, final);
            if (length == 0)
            {
                break;
            }

            if (_sent == MaxChunks)
            {
                limitReached = true;
                _closed = true;
                _chunks.Writer.TryComplete();
                offset = _pending.Length;
                break;
            }

            _chunks.Writer.TryWrite(_pending.Substring(offset, length));
            offset += length;
            _sent++;
        }

        _pending = offset == 0 ? _pending : _pending[offset..];
        return limitReached;
    }

    // How much of the text from start goes into the next chunk: whole characters up to the size of a chunk, and none yet when all
    // of it fits and is not to be sent. A surrogate pair is never split between chunks, so a high surrogate left at the end waits
    // for its other half, unless nothing more is coming.
    private static int ChunkLength(string text, int start, bool all, bool final)
    {
        var bytes = 0;
        var length = 0;
        while (start + length < text.Length)
        {
            var index = start + length;
            var pair = char.IsHighSurrogate(text[index]) && index + 1 < text.Length && char.IsLowSurrogate(text[index + 1]);
            var size = pair ? 4 : text[index] < 0x80 ? 1 : text[index] < 0x800 ? 2 : 3;
            if (bytes + size > MaxChunkBytes)
            {
                return length;
            }

            bytes += size;
            length += pair ? 2 : 1;
        }

        if (!all)
        {
            return 0;
        }

        return !final && length > 0 && char.IsHighSurrogate(text[start + length - 1]) ? length - 1 : length;
    }

    private void Arm()
    {
        if (_timerArmed)
        {
            return;
        }

        _timerArmed = true;
        _timer ??= _time.CreateTimer(OnTimer, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _timer.Change(_sent < SlowAfterChunks ? _interval : _interval * 10, Timeout.InfiniteTimeSpan);
    }

    private void OnTimer(object? state)
    {
        var tooLong = false;
        lock (_gate)
        {
            _timerArmed = false;
            if (_closed)
            {
                return;
            }

            tooLong = Flush(all: true, final: false);
        }

        if (tooLong)
        {
            Log.StreamTooLong(_logger, _stepId, MaxChunks, MaxChunkBytes, _topic.Name, _topic.Channel);
        }
    }

    private async Task PublishAsync(IInngestRealtime realtime, CancellationToken cancellationToken)
    {
        try
        {
            await realtime.PublishStreamAsync(_topic, _chunks.Reader.ReadAllAsync(cancellationToken), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The call is being abandoned, and its own cancellation says so.
            Close(null);
        }
        catch (Exception exception)
        {
            // Writes after a failure are refused instead of piling up.
            Close(exception);
            Log.StreamNotPublished(_logger, _stepId, _topic.Name, _topic.Channel, exception.Message);
        }
    }

    private void Close(Exception? failure)
    {
        lock (_gate)
        {
            _closed = true;
            _chunks.Writer.TryComplete(failure);
        }
    }
}
