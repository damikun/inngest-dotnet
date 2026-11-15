// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using System.Buffers;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.ExceptionServices;
using System.Text;

namespace Inngest.Realtime;

// The body of a streamed publish, sent chunked as the text arrives. Inngest reads it 1 KB at a time, relays each read as
// a chunk and stops reading after 1,000 reads, so a producer that yields a few characters at a time, as a model does,
// would use up the stream after a page of text. The text is therefore gathered into pieces, which go out when they are
// full or when their text has waited for a short interval, and the interval grows once the stream is long, so that the
// pieces last. A piece holds whole characters below 1 KB, which keeps a character from being split between two chunks.
internal sealed class TextStreamContent : HttpContent
{
    // The reads Inngest relays of one stream.
    public const int MaxPieces = 1000;

    private const int MaxPieceBytes = 1024;

    // After this many pieces, text waits ten times as long before it goes out in a piece that is not full.
    private const int SlowAfterPieces = 500;

    // How long text waits for more before it goes out in a piece that is not full.
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(100);

    private readonly IAsyncEnumerable<string> _chunks;
    private readonly TimeProvider _time;
    private readonly Action? _outgrown;
    private readonly StringBuilder _text = new();
    private int _sent;

    // outgrown is told once when the stream has more pieces than Inngest relays.
    public TextStreamContent(IAsyncEnumerable<string> chunks, TimeProvider time, Action? outgrown = null)
    {
        _chunks = chunks;
        _time = time;
        _outgrown = outgrown;
        Headers.ContentType = new MediaTypeHeaderValue("text/stream");
    }

    // Everything read from the chunks so far.
    public string Text => _text.ToString();

    // The pieces written so far.
    public int Pieces { get; private set; }

    // What the producer of the chunks threw. It is the caller's to see as it is, such as a NonRetriableException of a model
    // that gave up, whatever the handlers of the request make of it on the way.
    public ExceptionDispatchInfo? Failure { get; private set; }

    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
        SerializeToStreamAsync(stream, context, CancellationToken.None);

    protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
    {
        // The chunks are read as they are sent. A handler that sends the request again, such as a retry policy, would
        // read them from the start once more, which calls their producer, such as a model, again and streams the text to
        // the subscribers twice.
        if (Interlocked.Exchange(ref _sent, 1) != 0)
        {
            throw new InvalidOperationException("A streamed publish is sent once: sending it again would read its chunks again.");
        }

        using var piece = new Piece();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var chunks = _chunks.GetAsyncEnumerator(stop.Token);

        // The read of the next chunk, which goes on while a piece that has waited long enough is written.
        Task<bool>? next = null;
        try
        {
            while (true)
            {
                next ??= chunks.MoveNextAsync().AsTask();
                if (piece.Length > 0 && !next.IsCompleted && !await ArrivesInTimeAsync(next, piece.StartedAt, stop.Token).ConfigureAwait(false))
                {
                    await WriteAsync(stream, piece, stop.Token).ConfigureAwait(false);
                    continue;
                }

                if (!await ReadAsync(next).ConfigureAwait(false))
                {
                    break;
                }

                next = null;
                var chunk = chunks.Current;
                if (string.IsNullOrEmpty(chunk))
                {
                    continue;
                }

                _text.Append(chunk);
                var remaining = chunk.AsMemory();
                while (!remaining.IsEmpty)
                {
                    remaining = piece.Add(remaining, _time.GetTimestamp());
                    if (piece.IsFull)
                    {
                        await WriteAsync(stream, piece, stop.Token).ConfigureAwait(false);
                    }
                }
            }

            piece.End();
            if (piece.Length > 0)
            {
                await WriteAsync(stream, piece, stop.Token).ConfigureAwait(false);
            }
        }
        catch
        {
            // The producer may be at work on the next chunk, and it cannot be disposed until it has stopped.
            await stop.CancelAsync().ConfigureAwait(false);
            throw;
        }
        finally
        {
            if (next is { IsCompleted: false })
            {
                await ((Task)next).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }

            await chunks.DisposeAsync().ConfigureAwait(false);
        }
    }

    protected override bool TryComputeLength(out long length)
    {
        length = -1;
        return false;
    }

    private async Task<bool> ReadAsync(Task<bool> next)
    {
        try
        {
            return await next.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            Failure = ExceptionDispatchInfo.Capture(exception);
            throw;
        }
    }

    // Whether the next chunk arrives before the text of the piece has waited for the interval.
    private async Task<bool> ArrivesInTimeAsync(Task<bool> next, long startedAt, CancellationToken cancellationToken)
    {
        var wait = (Pieces < SlowAfterPieces ? Interval : Interval * 10) - _time.GetElapsedTime(startedAt);
        if (wait <= TimeSpan.Zero)
        {
            return false;
        }

        using var timer = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var first = await Task.WhenAny(next, Task.Delay(wait, _time, timer.Token)).ConfigureAwait(false);
        await timer.CancelAsync().ConfigureAwait(false);
        return first == next;
    }

    private async Task WriteAsync(Stream stream, Piece piece, CancellationToken cancellationToken)
    {
        if (++Pieces == MaxPieces + 1)
        {
            _outgrown?.Invoke();
        }

        await stream.WriteAsync(piece.Bytes, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        piece.Clear();
    }

    // The bytes of the next piece. The encoder keeps the first half of a surrogate pair until the other half arrives, so
    // a piece ends on a whole character.
    private sealed class Piece : IDisposable
    {
        // The most bytes one character takes in UTF-8.
        private const int MaxCharacterBytes = 4;

        private readonly byte[] _buffer = ArrayPool<byte>.Shared.Rent(MaxPieceBytes);
        private readonly Encoder _encoder = Encoding.UTF8.GetEncoder();

        public int Length { get; private set; }

        // When the first text of the piece arrived.
        public long StartedAt { get; private set; }

        public bool IsFull => MaxPieceBytes - Length < MaxCharacterBytes;

        public ReadOnlyMemory<byte> Bytes => _buffer.AsMemory(0, Length);

        // Takes as much of the text as fits and returns the rest. A piece that is not full has room for any character.
        public ReadOnlyMemory<char> Add(ReadOnlyMemory<char> text, long now)
        {
            _encoder.Convert(text.Span, _buffer.AsSpan(Length, MaxPieceBytes - Length), flush: false, out var charsUsed, out var bytesUsed, out _);
            if (Length == 0 && bytesUsed > 0)
            {
                StartedAt = now;
            }

            Length += bytesUsed;
            return text[charsUsed..];
        }

        // Adds the first half of a surrogate pair whose other half never came, as the replacement character.
        public void End()
        {
            _encoder.Convert([], _buffer.AsSpan(Length, MaxPieceBytes - Length), flush: true, out _, out var bytesUsed, out _);
            Length += bytesUsed;
        }

        public void Clear() => Length = 0;

        public void Dispose() => ArrayPool<byte>.Shared.Return(_buffer);
    }
}
