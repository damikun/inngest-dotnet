// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using System.Buffers;

namespace Inngest.Internal;

// An IBufferWriter backed by ArrayPool, for request and response bodies that can reach megabytes. Dispose returns
// the buffer, after which WrittenMemory must not be used.
internal sealed class PooledBufferWriter : IBufferWriter<byte>, IDisposable
{
    private const int MinimumSize = 256;

    private byte[] _buffer;
    private int _written;

    public PooledBufferWriter(int initialCapacity = MinimumSize)
    {
        _buffer = ArrayPool<byte>.Shared.Rent(Math.Max(initialCapacity, MinimumSize));
    }

    public ReadOnlyMemory<byte> WrittenMemory => _buffer.AsMemory(0, _written);

    public ReadOnlySpan<byte> WrittenSpan => _buffer.AsSpan(0, _written);

    public int WrittenCount => _written;

    public void Advance(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (_written > _buffer.Length - count)
        {
            throw new InvalidOperationException("Cannot advance past the end of the buffer.");
        }

        _written += count;
    }

    public Memory<byte> GetMemory(int sizeHint = 0)
    {
        EnsureCapacity(sizeHint);
        return _buffer.AsMemory(_written);
    }

    public Span<byte> GetSpan(int sizeHint = 0)
    {
        EnsureCapacity(sizeHint);
        return _buffer.AsSpan(_written);
    }

    public void Write(ReadOnlySpan<byte> data)
    {
        data.CopyTo(GetSpan(data.Length));
        _written += data.Length;
    }

    public void Clear() => _written = 0;

    public void Dispose()
    {
        var buffer = _buffer;
        _buffer = [];
        _written = 0;
        if (buffer.Length > 0)
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private void EnsureCapacity(int sizeHint)
    {
        ObjectDisposedException.ThrowIf(_buffer.Length == 0, this);
        if (sizeHint < 1)
        {
            sizeHint = 1;
        }

        if (_buffer.Length - _written >= sizeHint)
        {
            return;
        }

        var newSize = Math.Max(_written + sizeHint, _buffer.Length * 2);
        var next = ArrayPool<byte>.Shared.Rent(newSize);
        _buffer.AsSpan(0, _written).CopyTo(next);
        ArrayPool<byte>.Shared.Return(_buffer);
        _buffer = next;
    }
}
