// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using System.Text.Json;
using Inngest.Internal;

namespace Inngest.Middleware;

// A JSON value after the payload transforms. A rewritten value lives in a document over a pooled buffer, both
// released on Dispose; a value no transform rewrote is the original element, which the caller keeps alive.
internal readonly struct TransformedJson : IDisposable
{
    private readonly JsonDocument? _document;
    private readonly PooledBufferWriter? _buffer;

    public TransformedJson(JsonElement original)
    {
        Value = original;
    }

    public TransformedJson(JsonDocument document, PooledBufferWriter buffer)
    {
        _document = document;
        _buffer = buffer;
        Value = document.RootElement;
    }

    public JsonElement Value { get; }

    public bool IsRewritten => _buffer is not null;

    // The bytes a transform wrote; empty when none rewrote the value.
    public ReadOnlySpan<byte> RewrittenJson => _buffer is null ? default : _buffer.WrittenSpan;

    public void Dispose()
    {
        _document?.Dispose();
        _buffer?.Dispose();
    }
}
