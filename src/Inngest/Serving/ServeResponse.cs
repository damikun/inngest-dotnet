// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using Inngest.Internal;

namespace Inngest;

/// <summary>
/// The answer of <see cref="InngestCommHandler"/> to write back to Inngest: the status, the headers, the signature
/// among them, and the body, which are sent as they are. Dispose it once the body has been written, since the body may
/// live in a pooled buffer that the response owns.
/// </summary>
public sealed class ServeResponse : IDisposable
{
    private IDisposable? _owner;

    internal ServeResponse(int statusCode, IReadOnlyList<KeyValuePair<string, string>> headers, PooledBufferWriter body)
        : this(statusCode, headers, body.WrittenMemory, body)
    {
    }

    // owner is disposed with the response, so body can point into memory it holds, such as an engine result.
    internal ServeResponse(int statusCode, IReadOnlyList<KeyValuePair<string, string>> headers, ReadOnlyMemory<byte> body, IDisposable? owner = null)
    {
        StatusCode = statusCode;
        Headers = headers;
        Body = body;
        _owner = owner;
    }

    /// <summary>The HTTP status code.</summary>
    public int StatusCode { get; }

    /// <summary>The headers to send, Content-Type included.</summary>
    public IReadOnlyList<KeyValuePair<string, string>> Headers { get; }

    /// <summary>The body, JSON or empty. It is only valid until the response is disposed.</summary>
    public ReadOnlyMemory<byte> Body { get; }

    /// <summary>Returns the body's buffer to its pool.</summary>
    public void Dispose()
    {
        var owner = _owner;
        _owner = null;
        owner?.Dispose();
    }
}
