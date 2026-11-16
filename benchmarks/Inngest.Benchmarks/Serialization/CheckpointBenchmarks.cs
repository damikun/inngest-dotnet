using System.Text.Json;
using BenchmarkDotNet.Attributes;
using Inngest.Benchmarks.Support;
using Inngest.Configuration;
using Inngest.Execution;
using Inngest.Internal;
using Inngest.Json;

namespace Inngest.Benchmarks.Serialization;

// Buffering a checkpointed step: its op is kept for Inngest and its output is handed back canonicalized, as a replay
// reads it. Parse is what reading the output back cost before it was canonicalized. Written is a step result the way
// the app's serializer writes it, with properties in declaration order and non-ASCII text escaped; Canonical is one
// that is already in Inngest's form, such as a value read from an event.
[MemoryDiagnoser]
public class CheckpointBenchmarks
{
    private JsonSerializerOptions _options = null!;
    private Checkpointer _checkpointer = null!;
    private byte[] _written = null!;
    private byte[] _canonical = null!;

    [Params("small", "500KB")]
    public string Output { get; set; } = "small";

    [GlobalSetup]
    public void Setup()
    {
        _options = InngestSettings.Create(new InngestOptions { AppId = "shop", IsDev = true }, static _ => null).SerializerOptions;
        _checkpointer = Checkpointer.ForEndpoint(TimeProvider.System, _options);

        var output = Output == "small"
            ? JsonSerializer.SerializeToUtf8Bytes(new Payment("ch_000001", "succeeded", 1999, 19.99m, "EUR", Created), _options)
            : JsonSerializer.SerializeToUtf8Bytes(Batch(800), _options);
        using var canonical = new PooledBufferWriter();
        JsonCanonicalizer.Write(output, canonical);

        _written = Op(output);
        _canonical = Op(canonical.WrittenSpan);
        var expected = System.Text.Encoding.UTF8.GetString(canonical.WrittenSpan);
        Expect.That(!output.AsSpan().SequenceEqual(canonical.WrittenSpan), "the written output is not canonical");
        Expect.Equal(expected, Add(_written).GetRawText(), "the canonicalized output");
        Expect.Equal(expected, Add(_canonical).GetRawText(), "the canonical output");
    }

    [GlobalCleanup]
    public void Cleanup() => _checkpointer.Dispose();

    [Benchmark(Baseline = true)]
    public JsonValueKind Parse()
    {
        var reader = new Utf8JsonReader(_written, new JsonReaderOptions { MaxDepth = JsonPayload.DocumentOptions(_options).MaxDepth });
        return JsonElement.ParseValue(ref reader).GetProperty("data").ValueKind;
    }

    [Benchmark]
    public JsonValueKind Written() => Add(_written).ValueKind;

    [Benchmark]
    public JsonValueKind Canonical() => Add(_canonical).ValueKind;

    private static readonly DateTimeOffset Created = new(2026, 10, 29, 10, 0, 0, TimeSpan.Zero);

    private JsonElement Add(byte[] op)
    {
        var data = _checkpointer.Add(op);
        _checkpointer.Discard();
        return data;
    }

    private static byte[] Op(ReadOnlySpan<byte> output)
    {
        using var body = new PooledBufferWriter();
        using (var writer = new Utf8JsonWriter(body))
        {
            writer.WriteStartObject();
            writer.WriteString("id", "6f1c0b3a2d9e8f7a6b5c4d3e2f1a0b9c8d7e6f5a");
            writer.WriteString("op", "StepRun");
            writer.WriteString("name", "export-orders");
            writer.WritePropertyName("data");
            writer.WriteRawValue(output, skipInputValidation: true);
            writer.WriteStartObject("timing");
            writer.WriteNumber("a", 1793268000000000000);
            writer.WriteNumber("b", 1500000);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return body.WrittenSpan.ToArray();
    }

    private static OrderBatch Batch(int count)
    {
        string[] cities = ["Praha – Nové Město", "Brno-střed", "Plzeň", "Köln", "Zürich", "Wien"];
        var orders = new List<Order>(count);
        for (var i = 0; i < count; i++)
        {
            orders.Add(new Order(
                $"ord_{i:D6}",
                new Customer($"cus_{i % 97:D4}", $"Zákazník {i}", $"customer{i}@example.com", i % 3 == 0),
                i % 4 == 0 ? "paid" : "pending",
                [
                    new Line("TSHIRT-M-BLK", "T-shirt \"Classic\" M", 2, 29.95m, 0.21),
                    new Line("MUG-STD", "Mug 330 ml", 1, 12.5m, 0.21),
                    new Line("STICKER-PACK", "Stickers <5 pack>", 4, 4.5m, 0.15),
                ],
                new Address("12 Example Street", cities[i % cities.Length], "110 00", "CZ"),
                i % 5 == 0 ? null : "Leave at the door & ring twice",
                Created.AddMinutes(i)));
        }

        return new OrderBatch("batch_0001", count, orders);
    }

    private sealed record Payment(string Id, string Status, long AmountCents, decimal Amount, string Currency, DateTimeOffset CreatedAt);

    private sealed record OrderBatch(string Id, int Count, IReadOnlyList<Order> Orders);

    private sealed record Order(
        string Id,
        Customer Customer,
        string Status,
        IReadOnlyList<Line> Lines,
        Address ShippingAddress,
        string? Note,
        DateTimeOffset PlacedAt);

    private sealed record Customer(string Id, string Name, string Email, bool Vip);

    private sealed record Line(string Sku, string Title, int Quantity, decimal UnitPrice, double TaxRate);

    private sealed record Address(string Line1, string City, string PostalCode, string Country);
}
