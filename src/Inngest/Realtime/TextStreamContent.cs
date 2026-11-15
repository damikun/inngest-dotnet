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
// Once Inngest has stopped reading, it takes 256 KB more and then closes the connection, so the body ends after the
// pieces it relays, and the chunks it leaves are read by ReadToEndAsync.
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
    private readonly CancellationToken _cancellationToken;
    private readonly StringBuilder _text = new();
    private int _sent;

    // The chunks the body left unread when it reached the limit.
    private ChunkReader? _rest;

    // outgrown is told once when the stream has more pieces than Inngest relays. The cancellation token stops the
    // producer of the chunks, also while ReadToEndAsync reads what the body left.
    public TextStreamContent(IAsyncEnumerable<string> chunks, TimeProvider time, Action? outgrown = null, CancellationToken cancellationToken = default)
    {
        _chunks = chunks;
        _time = time;
        _outgrown = outgrown;
        _cancellationToken = cancellationToken;
        Headers.ContentType = new MediaTypeHeaderValue("text/stream");
    }

    // Everything read from the chunks so far.
    public string Text => _text.ToString();

    // The pieces written so far.
    public int Pieces { get; private set; }

    // What the producer of the chunks threw. It is the caller's to see as it is, such as a NonRetriableException of a model
    // that gave up, whatever the handlers of the request make of it on the way.
    public ExceptionDispatchInfo? Failure { get; private set; }

    // Reads the chunks the body left when it reached the limit, and returns all the text.
    public async Task<string> ReadToEndAsync()
    {
        if (Interlocked.Exchange(ref _rest, null) is { } rest)
        {
            await using (rest.ConfigureAwait(false))
            {
                while (await ReadAsync(rest).ConfigureAwait(false))
                {
                    _text.Append(rest.Current);
                    rest.Token.ThrowIfCancellationRequested();
                }
            }
        }

        return Text;
    }

    // Stops the producer of the chunks the body left, for a request that failed after it reached the limit.
    public async Task AbandonAsync()
    {
        if (Interlocked.Exchange(ref _rest, null) is { } rest)
        {
            await rest.StopAsync().ConfigureAwait(false);
            await rest.DisposeAsync().ConfigureAwait(false);
        }
    }

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
        var reader = new ChunkReader(_chunks, _cancellationToken, cancellationToken);
        var outgrown = false;
        try
        {
            while (true)
            {
                if (piece.Length > 0 && !reader.Next.IsCompleted && !await ArrivesInTimeAsync(reader.Next, piece.StartedAt, reader.Token).ConfigureAwait(false))
                {
                    if (!await WriteAsync(stream, piece, reader.Token).ConfigureAwait(false))
                    {
                        outgrown = true;
                        return;
                    }

                    continue;
                }

                if (!await ReadAsync(reader).ConfigureAwait(false))
                {
                    break;
                }

                var chunk = reader.Current;
                if (string.IsNullOrEmpty(chunk))
                {
                    continue;
                }

                _text.Append(chunk);
                var remaining = chunk.AsMemory();
                while (!remaining.IsEmpty)
                {
                    remaining = piece.Add(remaining, _time.GetTimestamp());
                    if (piece.IsFull && !await WriteAsync(stream, piece, reader.Token).ConfigureAwait(false))
                    {
                        outgrown = true;
                        return;
                    }
                }
            }

            piece.End();
            if (piece.Length > 0)
            {
                await WriteAsync(stream, piece, reader.Token).ConfigureAwait(false);
            }
        }
        catch
        {
            // The producer may be at work on the next chunk, and it cannot be disposed until it has stopped.
            await reader.StopAsync().ConfigureAwait(false);
            throw;
        }
        finally
        {
            if (outgrown)
            {
                reader.Detach();
                _rest = reader;
            }
            else
            {
                await reader.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    protected override bool TryComputeLength(out long length)
    {
        length = -1;
        return false;
    }

    private async Task<bool> ReadAsync(ChunkReader reader)
    {
        try
        {
            return await reader.ReadAsync().ConfigureAwait(false);
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

    // Writes the piece, unless the stream already has all the pieces Inngest relays.
    private async Task<bool> WriteAsync(Stream stream, Piece piece, CancellationToken cancellationToken)
    {
        if (Pieces == MaxPieces)
        {
            _outgrown?.Invoke();
            return false;
        }

        Pieces++;
        await stream.WriteAsync(piece.Bytes, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        piece.Clear();
        return true;
    }

    // Reads the chunks one ahead, so that a piece can go out while the next chunk is on its way. The producer stops when
    // the caller's token is cancelled, or the request's until the reader is detached from the request.
    private sealed class ChunkReader : IAsyncDisposable
    {
        private readonly CancellationTokenSource _stop;
        private readonly IAsyncEnumerator<string> _chunks;
        private readonly CancellationTokenRegistration _request;
        private Task<bool>? _next;

        public ChunkReader(IAsyncEnumerable<string> chunks, CancellationToken cancellationToken, CancellationToken request)
        {
            _stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _request = request.UnsafeRegister(static stop => ((CancellationTokenSource)stop!).Cancel(), _stop);
            _chunks = chunks.GetAsyncEnumerator(_stop.Token);
        }

        public CancellationToken Token => _stop.Token;

        // The read of the next chunk, begun if it has not been.
        public Task<bool> Next => _next ??= _chunks.MoveNextAsync().AsTask();

        public string Current => _chunks.Current;

        public async Task<bool> ReadAsync()
        {
            var read = await Next.ConfigureAwait(false);
            _next = null;
            return read;
        }

        public Task StopAsync() => _stop.CancelAsync();

        // Lets the producer go on after the request has ended.
        public void Detach() => _request.Dispose();

        public async ValueTask DisposeAsync()
        {
            _request.Dispose();
            if (_next is { IsCompleted: false } next)
            {
                await ((Task)next).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }

            await _chunks.DisposeAsync().ConfigureAwait(false);
            _stop.Dispose();
        }
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
