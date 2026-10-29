// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using Inngest.Internal;

namespace Inngest.Serving;

// A response to write back to Inngest. Dispose it after the body has been written; the body may live in a pooled
// buffer that the response owns.
internal sealed class ServeResponse : IDisposable
{
    private IDisposable? _owner;

    public ServeResponse(int statusCode, IReadOnlyList<KeyValuePair<string, string>> headers, PooledBufferWriter body)
        : this(statusCode, headers, body.WrittenMemory, body)
    {
    }

    // owner is disposed with the response, so body can point into memory it holds, such as an engine result.
    public ServeResponse(int statusCode, IReadOnlyList<KeyValuePair<string, string>> headers, ReadOnlyMemory<byte> body, IDisposable? owner = null)
    {
        StatusCode = statusCode;
        Headers = headers;
        Body = body;
        _owner = owner;
    }

    public int StatusCode { get; }

    public IReadOnlyList<KeyValuePair<string, string>> Headers { get; }

    public ReadOnlyMemory<byte> Body { get; }

    public void Dispose()
    {
        var owner = _owner;
        _owner = null;
        owner?.Dispose();
    }
}
