// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using System.Buffers;
using Google.Protobuf;
using Inngest.Connect.Protocol;

namespace Inngest.Connect;

// Every WebSocket message is a ConnectMessage {kind = 1, payload = 2}. The envelope is written and read here
// directly, so that a payload, which can be megabytes, goes straight into the frame and is parsed straight out of the
// receive buffer instead of passing through a ByteString copy on the way.
internal static class Frames
{
    private const int KindField = 1;
    private const int PayloadField = 2;

    public static Frame Create(GatewayMessageType kind, IMessage? payload = null)
    {
        var payloadSize = payload?.CalculateSize() ?? 0;
        var frame = Frame.Rent(KindSize(kind) + PayloadSize(payloadSize));
        var output = frame.CreateWriter();
        WriteKind(output, kind);
        if (payload is not null && payloadSize > 0)
        {
            output.WriteTag(PayloadField, WireFormat.WireType.LengthDelimited);
            output.WriteMessage(payload);
        }

        output.Flush();
        return frame;
    }

    // For a payload that is already encoded, such as a reply that is also kept for flushing over HTTP.
    public static Frame Create(GatewayMessageType kind, ReadOnlyMemory<byte> payload)
    {
        var frame = Frame.Rent(KindSize(kind) + PayloadSize(payload.Length));
        var output = frame.CreateWriter();
        WriteKind(output, kind);
        if (!payload.IsEmpty)
        {
            output.WriteTag(PayloadField, WireFormat.WireType.LengthDelimited);
            output.WriteBytes(UnsafeByteOperations.UnsafeWrap(payload));
        }

        output.Flush();
        return frame;
    }

    // Accepts the fields in any order and skips unknown ones, as a protobuf parser must. A message without a kind is
    // GATEWAY_HELLO, whose encoding is zero bytes.
    public static bool TryRead(ReadOnlySpan<byte> frame, out GatewayMessageType kind, out ReadOnlySpan<byte> payload)
    {
        kind = GatewayMessageType.GatewayHello;
        payload = default;
        var position = 0;
        while (position < frame.Length)
        {
            if (!TryReadVarint(frame, ref position, out var tag) || tag >> 3 == 0)
            {
                return false;
            }

            var field = tag >> 3;
            switch ((WireFormat.WireType)(tag & 7))
            {
                case WireFormat.WireType.Varint:
                    if (!TryReadVarint(frame, ref position, out var value))
                    {
                        return false;
                    }

                    if (field == KindField)
                    {
                        kind = (GatewayMessageType)(int)value;
                    }

                    break;
                case WireFormat.WireType.LengthDelimited:
                    if (!TryReadVarint(frame, ref position, out var length) || length > (ulong)(frame.Length - position))
                    {
                        return false;
                    }

                    if (field == PayloadField)
                    {
                        payload = frame.Slice(position, (int)length);
                    }

                    position += (int)length;
                    break;
                case WireFormat.WireType.Fixed64 when frame.Length - position >= 8:
                    position += 8;
                    break;
                case WireFormat.WireType.Fixed32 when frame.Length - position >= 4:
                    position += 4;
                    break;
                default:
                    return false;
            }
        }

        return true;
    }

    // Proto3 leaves out fields that hold their default, so kind 0 is not written.
    private static int KindSize(GatewayMessageType kind) =>
        kind == GatewayMessageType.GatewayHello
            ? 0
            : CodedOutputStream.ComputeTagSize(KindField) + CodedOutputStream.ComputeEnumSize((int)kind);

    private static int PayloadSize(int size) =>
        size == 0 ? 0 : CodedOutputStream.ComputeTagSize(PayloadField) + CodedOutputStream.ComputeLengthSize(size) + size;

    private static void WriteKind(CodedOutputStream output, GatewayMessageType kind)
    {
        if (kind != GatewayMessageType.GatewayHello)
        {
            output.WriteTag(KindField, WireFormat.WireType.Varint);
            output.WriteEnum((int)kind);
        }
    }

    private static bool TryReadVarint(ReadOnlySpan<byte> data, ref int position, out ulong value)
    {
        value = 0;
        for (var shift = 0; shift < 64 && position < data.Length; shift += 7)
        {
            var current = data[position++];
            value |= (ulong)(current & 0x7F) << shift;
            if (current < 0x80)
            {
                return true;
            }
        }

        return false;
    }
}

// An encoded message in a buffer rented from the pool. Whoever holds it last returns it with Dispose.
internal sealed class Frame : IDisposable
{
    private byte[]? _buffer;

    private Frame(byte[] buffer, int length)
    {
        _buffer = buffer;
        Length = length;
    }

    public int Length { get; }

    public ReadOnlyMemory<byte> Memory => (_buffer ?? throw new ObjectDisposedException(nameof(Frame))).AsMemory(0, Length);

    public static Frame Rent(int length) => new(ArrayPool<byte>.Shared.Rent(Math.Max(length, 1)), length);

    // Writes from the start of the buffer. A rented buffer can be longer than the frame; the sizes computed up front
    // keep the writes within Length.
    public CodedOutputStream CreateWriter() => new(_buffer!);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _buffer, null) is { } buffer)
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
