using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Inngest.IntegrationTests.Evaluation.Functions;

internal sealed record NullResult(bool TextWasNull, bool RecordWasNull, int? Number);

// Numbers stay within what a JSON double holds exactly: Inngest canonicalizes the JSON of every call (RFC 8785), which
// turns each number into a double. 2^53 + 1 comes back as 2^53, and long.MaxValue as a number no long can hold.
internal sealed record Primitives(
    int Int,
    long Long,
    double Double,
    decimal Decimal,
    bool Bool,
    string Text,
    char Char,
    DateTimeOffset Time,
    DateOnly Date,
    TimeSpan Duration,
    Guid Guid,
    Uri Uri,
    DayOfWeek Day);

internal enum OrderState
{
    Draft,
    Paid,
    Shipped,
}

internal sealed record Address(string Street, string City, string? Region);

internal sealed record OrderLine(string Sku, int Quantity, decimal Price);

internal sealed record Order(
    string Id,
    OrderState State,
    Address ShipTo,
    IReadOnlyList<OrderLine> Lines,
    IReadOnlyDictionary<string, string> Tags,
    int[][] Matrix,
    Order? Previous);

internal sealed record LargeResult(int Length, string Sha256);

internal sealed record UnicodeEvent(string TestId, string Text) : IScenarioEvent;

internal sealed record UnicodeResult(string Echoed, string FromSecondStep, int Length);

// What step results look like after their round trip through Inngest: null, primitives, records, large and Unicode data.
internal static class StepDataFunctions
{
    public const string Null = "eval/step.null";
    public const string PrimitiveValues = "eval/step.primitives";
    public const string Record = "eval/step.record";
    public const string Large = "eval/step.large";
    public const string Unicode = "eval/step.unicode";

    public static readonly Primitives SamplePrimitives = new(
        int.MaxValue,
        (1L << 53) - 1,
        0.1 + 0.2,
        12345.6789m,
        true,
        "line\nbreak \"quoted\" \\ back",
        'Ž',
        new DateTimeOffset(2026, 10, 29, 13, 45, 12, 345, TimeSpan.FromHours(2)),
        new DateOnly(2024, 2, 29),
        TimeSpan.FromMinutes(90.5),
        Guid.Parse("8c2b8e7a-3f0e-4a4e-9a55-6a1f1d6c2e10"),
        new Uri("https://example.com/path?q=1#frag"),
        DayOfWeek.Friday);

    public static readonly Order SampleOrder = new(
        "order-1",
        OrderState.Shipped,
        new Address("Main Street 1", "Prague", null),
        [new OrderLine("sku-1", 2, 9.99m), new OrderLine("sku-2", 1, 120m)],
        new Dictionary<string, string> { ["channel"] = "web", ["coupon"] = string.Empty },
        [[1, 2], [3], []],
        new Order("order-0", OrderState.Draft, new Address("Old Street", "Brno", "JM"), [], new Dictionary<string, string>(), [], null));

    // Deterministic text of the given size, different for every test.
    public static string LargeText(string testId, int length)
    {
        var text = new StringBuilder(length);
        var counter = 0;
        while (text.Length < length)
        {
            text.Append(testId).Append(':').Append(counter++).Append(' ');
        }

        return text.ToString(0, length);
    }

    public static string Sha256(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    public static void Add(IInngestBuilder inngest)
    {
        // A step whose result is null must count as done, not as a step that never ran.
        inngest.AddFunction<RunEvent>(
            new FunctionOptions { Id = "step-null", Triggers = [Trigger.Event(Null)] },
            static async (context, _) =>
            {
                var observed = context.Observed();
                var text = await context.Step.RunAsync("null-text", () =>
                {
                    observed.Record("null-text");
                    return (string?)null;
                });
                var record = await context.Step.RunAsync("null-record", () =>
                {
                    observed.Record("null-record");
                    return (Address?)null;
                });
                var number = await context.Step.RunAsync("null-number", () =>
                {
                    observed.Record("null-number");
                    return (int?)null;
                });

                return await context.Step.RunAsync("after", () => new NullResult(text is null, record is null, number));
            });

        inngest.AddFunction<RunEvent>(
            new FunctionOptions { Id = "step-primitives", Triggers = [Trigger.Event(PrimitiveValues)] },
            static async (context, _) =>
            {
                var value = SamplePrimitives;
                var parts = await Task.WhenAll(
                    context.Step.RunAsync("int", () => (object)value.Int),
                    context.Step.RunAsync("long", () => (object)value.Long),
                    context.Step.RunAsync("double", () => (object)value.Double),
                    context.Step.RunAsync("bool", () => (object)value.Bool));
                var text = await context.Step.RunAsync("text", () => value.Text);
                var time = await context.Step.RunAsync("time", () => value.Time);
                var guid = await context.Step.RunAsync("guid", () => value.Guid);
                var all = await context.Step.RunAsync("all", () => value);

                // Each typed result must equal the original; the object-typed ones come back as JSON elements.
                return new
                {
                    textEqual = text == value.Text,
                    timeEqual = time == value.Time,
                    guidEqual = guid == value.Guid,
                    allEqual = all == value,
                    untyped = parts.Select(part => part?.ToString()).ToArray(),
                    all,
                };
            });

        inngest.AddFunction<RunEvent>(
            new FunctionOptions { Id = "step-record", Triggers = [Trigger.Event(Record)] },
            static async (context, _) =>
            {
                var observed = context.Observed();
                var order = await context.Step.RunAsync("load-order", () =>
                {
                    observed.Record("load-order");
                    return SampleOrder;
                });

                // A later step sees the record exactly as the first step returned it.
                var total = await context.Step.RunAsync("total", () => order.Lines.Sum(line => line.Quantity * line.Price));
                return new { order, total, sameAsReturned = Equivalent(order, SampleOrder) };
            });

        inngest.AddFunction<ValueEvent>(
            new FunctionOptions { Id = "step-large", Triggers = [Trigger.Event(Large)] },
            static async (context, _) =>
            {
                var observed = context.Observed();
                var text = await context.Step.RunAsync("large", () =>
                {
                    observed.Record("large");
                    return LargeText(context.Event.Data.TestId, context.Event.Data.Value);
                });

                return await context.Step.RunAsync("measure", () => new LargeResult(text.Length, Sha256(text)));
            });

        // Unicode in the data and in the step ids, which are hashed as UTF-8.
        inngest.AddFunction<UnicodeEvent>(
            new FunctionOptions { Id = "step-unicode", Triggers = [Trigger.Event(Unicode)] },
            static async (context, _) =>
            {
                var observed = context.Observed();
                var echoed = await context.Step.RunAsync("krok-ř-🚀", () =>
                {
                    observed.Record("first");
                    return context.Event.Data.Text;
                });
                var second = await context.Step.RunAsync("步骤-二", () =>
                {
                    observed.Record("second");
                    return $"{echoed}|{echoed.Length}";
                });

                return new UnicodeResult(echoed, second, echoed.Length);
            });
    }

    // Records with collections compare by reference, so compare their JSON instead.
    private static bool Equivalent<T>(T actual, T expected) =>
        JsonSerializer.Serialize(actual) == JsonSerializer.Serialize(expected);
}
