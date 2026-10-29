using System.Text.Json;
using BenchmarkDotNet.Attributes;
using Inngest.Benchmarks.Support;
using Inngest.Functions;
using Inngest.Internal;
using Inngest.Registration;

namespace Inngest.Benchmarks.Serialization;

// The body of a sync (PUT) for functions that use every option that can be combined. Batching cannot be used
// together with several of them, so no function here batches.
[MemoryDiagnoser]
public class SyncBodyBenchmarks
{
    private static readonly Uri ServeUrl = new("https://shop.example.com/api/inngest");

    private static readonly Func<IServiceProvider, InngestFunction> NotRunnable =
        static _ => throw new NotSupportedException("A sync does not run functions.");

    private static readonly string[] ExpectedProperties =
    [
        "id", "name", "triggers", "steps", "concurrency", "throttle", "rateLimit", "idempotency", "debounce", "priority", "cancel", "timeouts",
        "singleton",
    ];

    private FunctionRegistry _registry = null!;

    [Params(1, 20)]
    public int Functions { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        var settings = InngestSettings.Create(new InngestOptions { AppId = "shop", AppVersion = "1.4.0" }, static _ => null);
        var definitions = Enumerable.Range(0, Functions).Select(static index => new FunctionDefinition(FullOptions(index), NotRunnable));
        _registry = new FunctionRegistry(definitions, settings);

        using var body = new PooledBufferWriter();
        _registry.WriteRegistration(body, ServeUrl, "aspnetcore");
        using var document = JsonDocument.Parse(body.WrittenMemory);
        var functions = Expect.Property(document.RootElement, "functions");
        Expect.Equal(Functions, functions.GetArrayLength(), "the number of functions in the sync body");
        foreach (var function in functions.EnumerateArray())
        {
            foreach (var name in ExpectedProperties)
            {
                Expect.Property(function, name);
            }
        }
    }

    [Benchmark]
    public int WriteSyncBody()
    {
        // The serve handler starts a sync body at this size.
        using var body = new PooledBufferWriter(4096);
        _registry.WriteRegistration(body, ServeUrl, "aspnetcore");
        return body.WrittenCount;
    }

    private static FunctionOptions FullOptions(int index) => new()
    {
        Id = $"process-order-{index}",
        Name = $"Process order {index}",
        Triggers =
        [
            Trigger.Event("shop/order.placed", "event.data.currency == 'EUR'"),
            Trigger.Cron("0 9 * * 1-5", "Europe/Prague"),
        ],
        Retries = 5,
        Concurrency =
        [
            new ConcurrencyLimit(10),
            new ConcurrencyLimit(1) { Key = "event.data.customerId", Scope = ConcurrencyScope.Environment },
        ],
        Throttle = new Throttle(5, TimeSpan.FromMinutes(1)) { Burst = 2, Key = "event.data.customerId" },
        RateLimit = new RateLimit(1, TimeSpan.FromHours(4)) { Key = "event.data.customerId" },
        Idempotency = "event.data.orderId",
        Debounce = new Debounce(TimeSpan.FromSeconds(5)) { Key = "event.data.customerId", Timeout = TimeSpan.FromMinutes(1) },
        Priority = new Priority("event.data.totalCents > 100000 ? 120 : 0"),
        Timeouts = new FunctionTimeouts { Start = TimeSpan.FromMinutes(10), Finish = TimeSpan.FromHours(1) },
        Singleton = new Singleton(SingletonMode.Skip) { Key = "event.data.orderId" },
        CancelOn =
        [
            new CancelOn("shop/order.cancelled") { Match = "data.orderId", Timeout = TimeSpan.FromHours(1) },
            new CancelOn("shop/customer.deleted") { If = "async.data.customerId == event.data.customerId", Timeout = TimeSpan.FromMinutes(1) },
        ],
    };
}
