using System.Reflection;
using System.Text.Json;
using Inngest.Internal;

namespace Inngest.Tests.Internal;

public sealed class PooledBufferWriterTests
{
    [Fact]
    public void Grows_while_keeping_written_bytes()
    {
        using var writer = new PooledBufferWriter(initialCapacity: 4);
        var data = Enumerable.Range(0, 10_000).Select(i => (byte)i).ToArray();

        foreach (var chunk in data.Chunk(333))
        {
            writer.Write(chunk);
        }

        writer.WrittenSpan.ToArray().ShouldBe(data);
    }

    [Fact]
    public void Works_as_a_json_writer_target()
    {
        using var buffer = new PooledBufferWriter();
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartObject();
            json.WriteString("op", "StepRun");
            json.WriteEndObject();
        }

        System.Text.Encoding.UTF8.GetString(buffer.WrittenSpan).ShouldBe("""{"op":"StepRun"}""");
    }

    [Fact]
    public void Cannot_be_used_after_dispose()
    {
        var writer = new PooledBufferWriter();
        writer.Dispose();

        Should.Throw<ObjectDisposedException>(() => writer.GetSpan(1));
        writer.WrittenCount.ShouldBe(0);
    }

    [Fact]
    public void Dispose_clears_bytes_that_Clear_hid()
    {
        var writer = new PooledBufferWriter();
        writer.Write("secret"u8);
        var rented = Buffer(writer);
        writer.Clear();
        writer.Dispose();

        rented.AsSpan(0, 6).ContainsAnyExcept((byte)0).ShouldBeFalse();
    }

    [Fact]
    public void A_grow_clears_the_buffer_it_returns()
    {
        var writer = new PooledBufferWriter(256);
        writer.Write("secret"u8);
        var first = Buffer(writer);

        writer.Write(new byte[300]);

        first.AsSpan(0, 6).ContainsAnyExcept((byte)0).ShouldBeFalse();
        writer.Dispose();
    }

    private static byte[] Buffer(PooledBufferWriter writer) =>
        (byte[])typeof(PooledBufferWriter).GetField("_buffer", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(writer)!;
}
