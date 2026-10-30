// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using System.Buffers;
using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace Inngest.Realtime;

// The body of a streamed publish, sent chunked as the text arrives. Inngest reads it 1 KB at a time and relays each read
// as a chunk, so every piece is written and flushed on its own, cut at a character boundary below 1 KB, which keeps a
// character from being split between two chunks while the pieces arrive no faster than Inngest reads them.
internal sealed class TextStreamContent : HttpContent
{
    private const int MaxPieceBytes = 1024;

    private readonly IAsyncEnumerable<string> _chunks;
    private readonly StringBuilder _text = new();

    public TextStreamContent(IAsyncEnumerable<string> chunks)
    {
        _chunks = chunks;
        Headers.ContentType = new MediaTypeHeaderValue("text/stream");
    }

    // Everything sent so far.
    public string Text => _text.ToString();

    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
        SerializeToStreamAsync(stream, context, CancellationToken.None);

    protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(MaxPieceBytes + 4);
        var encoder = Encoding.UTF8.GetEncoder();
        try
        {
            await foreach (var chunk in _chunks.WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                if (string.IsNullOrEmpty(chunk))
                {
                    continue;
                }

                _text.Append(chunk);
                var remaining = chunk.AsMemory();
                while (!remaining.IsEmpty)
                {
                    // Whole characters only: a surrogate pair left at the end of a piece waits for the next one.
                    encoder.Convert(remaining.Span, buffer.AsSpan(0, MaxPieceBytes), flush: false, out var charsUsed, out var bytesUsed, out _);
                    remaining = remaining[charsUsed..];
                    if (bytesUsed > 0)
                    {
                        await stream.WriteAsync(buffer.AsMemory(0, bytesUsed), cancellationToken).ConfigureAwait(false);
                        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                    }
                }
            }

            encoder.Convert([], buffer, flush: true, out _, out var lastBytes, out _);
            if (lastBytes > 0)
            {
                await stream.WriteAsync(buffer.AsMemory(0, lastBytes), cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    protected override bool TryComputeLength(out long length)
    {
        length = -1;
        return false;
    }
}
