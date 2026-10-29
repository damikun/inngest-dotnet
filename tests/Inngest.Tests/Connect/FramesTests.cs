using Google.Protobuf;
using Inngest.Connect;
using Inngest.Connect.Protocol;

namespace Inngest.Tests.Connect;

public sealed class FramesTests
{
    [Fact]
    public void Hello_without_a_payload_is_an_empty_frame()
    {
        using var frame = Frames.Create(GatewayMessageType.GatewayHello);

        frame.Length.ShouldBe(0);
        Frames.TryRead(frame.Memory.Span, out var kind, out var payload).ShouldBeTrue();
        kind.ShouldBe(GatewayMessageType.GatewayHello);
        payload.IsEmpty.ShouldBeTrue();
    }

    [Fact]
    public void Writes_exactly_what_the_generated_code_writes()
    {
        var ack = new WorkerRequestAckData { RequestId = "req-1", RunId = "run-1", StepId = "step" };

        using var frame = Frames.Create(GatewayMessageType.WorkerRequestAck, ack);

        var expected = new ConnectMessage { Kind = GatewayMessageType.WorkerRequestAck, Payload = ack.ToByteString() }.ToByteArray();
        frame.Memory.ToArray().ShouldBe(expected);
    }

    [Fact]
    public void Writes_an_encoded_payload_as_it_is()
    {
        var reply = new SDKResponse { RequestId = "req-1", Body = ByteString.CopyFromUtf8("\"done\""), Status = SDKResponseStatus.Done };
        var bytes = reply.ToByteArray();

        using var frame = Frames.Create(GatewayMessageType.WorkerReply, bytes);

        var message = ConnectMessage.Parser.ParseFrom(frame.Memory.Span);
        message.Kind.ShouldBe(GatewayMessageType.WorkerReply);
        SDKResponse.Parser.ParseFrom(message.Payload).ShouldBe(reply);
    }

    [Fact]
    public void Reads_what_the_generated_code_writes()
    {
        var request = new GatewayExecutorRequestData { RequestId = "req-1", RequestPayload = ByteString.CopyFromUtf8("{}") };
        var bytes = new ConnectMessage { Kind = GatewayMessageType.GatewayExecutorRequest, Payload = request.ToByteString() }.ToByteArray();

        Frames.TryRead(bytes, out var kind, out var payload).ShouldBeTrue();

        kind.ShouldBe(GatewayMessageType.GatewayExecutorRequest);
        GatewayExecutorRequestData.Parser.ParseFrom(payload).ShouldBe(request);
    }

    [Fact]
    public void Reads_a_message_without_a_payload()
    {
        var bytes = new ConnectMessage { Kind = GatewayMessageType.GatewayHeartbeat }.ToByteArray();

        Frames.TryRead(bytes, out var kind, out var payload).ShouldBeTrue();

        kind.ShouldBe(GatewayMessageType.GatewayHeartbeat);
        payload.IsEmpty.ShouldBeTrue();
    }

    [Fact]
    public void Reads_fields_in_any_order_and_skips_unknown_ones()
    {
        // payload "ab", an unknown varint field 7, an unknown fixed32 field 8, an unknown fixed64 field 9, then the kind.
        byte[] bytes = [0x12, 0x02, (byte)'a', (byte)'b', 0x38, 0x96, 0x01, 0x45, 1, 2, 3, 4, 0x49, 1, 2, 3, 4, 5, 6, 7, 8, 0x08, 0x0B];

        Frames.TryRead(bytes, out var kind, out var payload).ShouldBeTrue();

        kind.ShouldBe(GatewayMessageType.GatewayClosing);
        payload.ToArray().ShouldBe("ab"u8.ToArray());
    }

    [Theory]
    [InlineData(new byte[] { 0x08 })]
    [InlineData(new byte[] { 0x12, 0x05, 1, 2 })]
    [InlineData(new byte[] { 0x08, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x01 })]
    [InlineData(new byte[] { 0x00, 0x01 })]
    [InlineData(new byte[] { 0x0B, 0x01 })]
    [InlineData(new byte[] { 0x45, 1, 2 })]
    public void Rejects_frames_that_are_not_protobuf(byte[] bytes)
    {
        Frames.TryRead(bytes, out _, out _).ShouldBeFalse();
    }

    [Fact]
    public void A_frame_returns_its_buffer_once()
    {
        var frame = Frames.Create(GatewayMessageType.WorkerHeartbeat);

        frame.Dispose();
        frame.Dispose();

        Should.Throw<ObjectDisposedException>(() => frame.Memory);
    }
}
