using System.Text.Json;

namespace Inngest.Benchmarks.Support;

// The payloads are plain records with default serializer options, which is what most apps run with.
internal sealed record OrderPlaced(
    string OrderId,
    string CustomerId,
    string Currency,
    long TotalCents,
    IReadOnlyList<OrderLine> Lines,
    ShippingAddress ShippingAddress);

internal sealed record OrderLine(string Sku, int Quantity, long UnitPriceCents);

internal sealed record ShippingAddress(string Line1, string City, string PostalCode, string Country);

internal sealed record UserRef(string Id);

internal sealed record StepResult(string Id, string Status, long AmountCents, string Currency, DateTimeOffset CreatedAt);

internal sealed record Summary(int Steps, long TotalCents);

internal static class Samples
{
    public const string EventName = "shop/order.placed";

    private static readonly DateTimeOffset Created = new(2026, 10, 29, 10, 0, 0, TimeSpan.Zero);

    public static OrderPlaced Order(int index) => new(
        $"ord_{index:D6}",
        $"cus_{index % 97:D4}",
        "EUR",
        15990,
        [new OrderLine("TSHIRT-M-BLK", 2, 2995), new OrderLine("MUG-STD", 1, 1200), new OrderLine("STICKER-PACK", 4, 450)],
        new ShippingAddress("12 Example Street", "Prague", "11000", "CZ"));

    // The event as Inngest delivers it to a function, keys sorted.
    public static string EventJson() =>
        $$"""{"data":{{JsonSerializer.Serialize(Order(1), JsonSerializerOptions.Web)}},"id":"01JC8Y3N5W9X2K4M7P6Q8R1S0T","name":"{{EventName}}","ts":1793268000000,"user":{"id":"usr_000123"},"v":"2024-05-01"}""";

    public static StepResult Result(int index) => new($"ch_{index:D6}", "succeeded", 1000 + index, "EUR", Created);

    public static string ResultJson(int index) => JsonSerializer.Serialize(Result(index), JsonSerializerOptions.Web);

    // A memoized run step as Inngest sends it back.
    public static string MemoizedResultJson(int index) => $$"""{"data":{{ResultJson(index)}}}""";

    public static string[] StepIds(int count) => [.. Enumerable.Range(0, count).Select(static index => $"process-item-{index:D4}")];

    // The output of the benchmark functions when step i returned Result(i), for every i below count.
    public static string ExpectedSummaryJson(int count) =>
        $$"""{"steps":{{count}},"totalCents":{{Enumerable.Range(0, count).Sum(static index => Result(index).AmountCents)}}}""";
}
