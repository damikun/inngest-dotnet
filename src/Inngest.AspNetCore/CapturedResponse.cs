// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using System.IO.Pipelines;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

namespace Inngest.AspNetCore;

// Holds what a durable endpoint writes until the request's outcome is known: the response goes out once the endpoint
// has finished, and a redirect or the answer to Inngest replaces it otherwise. The status and headers stay on the real
// response, which has not started, so they can still be changed or cleared.
internal sealed class CapturedResponse : IHttpResponseBodyFeature
{
    private readonly HttpContext _context;
    private readonly IHttpResponseBodyFeature _original;
    private readonly MemoryStream _body = new();
    private PipeWriter? _writer;

    public CapturedResponse(HttpContext context)
    {
        _context = context;
        _original = context.Features.GetRequiredFeature<IHttpResponseBodyFeature>();
        context.Features.Set<IHttpResponseBodyFeature>(this);
    }

    public Stream Stream => _body;

    public PipeWriter Writer => _writer ??= PipeWriter.Create(_body, new StreamPipeWriterOptions(leaveOpen: true));

    // Valid once the writer has been flushed.
    public ReadOnlyMemory<byte> Body => _body.GetBuffer().AsMemory(0, (int)_body.Length);

    public void DisableBuffering()
    {
    }

    // Starting the response is what the capture holds back.
    public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task SendFileAsync(string path, long offset, long? count, CancellationToken cancellationToken = default) =>
        SendFileFallback.SendFileAsync(_body, path, offset, count, cancellationToken);

    public Task CompleteAsync() => FlushAsync();

    public async Task FlushAsync()
    {
        if (_writer is not null)
        {
            await _writer.FlushAsync().ConfigureAwait(false);
        }
    }

    // Puts the real response body back.
    public void Detach()
    {
        if (ReferenceEquals(_context.Features.Get<IHttpResponseBodyFeature>(), this))
        {
            _context.Features.Set(_original);
        }
    }
}
