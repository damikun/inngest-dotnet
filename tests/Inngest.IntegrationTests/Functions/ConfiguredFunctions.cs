namespace Inngest.IntegrationTests.Functions;

// Never triggered by a test; it exists so the sync tests can read its flow control back from the server. The cron
// schedule (midnight on January 1st) never fires during a test run.
internal sealed class ConfiguredFunction : InngestFunction<TestEvent>
{
    public static FunctionOptions Options { get; } = new()
    {
        Id = "configured",
        Name = "Flow control",
        Triggers =
        [
            Trigger.Event("itests/configured", "event.data.priority > 2"),
            Trigger.Cron("0 0 1 1 *", "Europe/Prague"),
        ],
        Retries = 5,
        Concurrency = [new ConcurrencyLimit(3) { Key = "event.data.testId" }],
        Throttle = new Throttle(10, TimeSpan.FromMinutes(1)) { Burst = 2, Key = "event.data.testId" },
        RateLimit = new RateLimit(100, TimeSpan.FromHours(1)) { Key = "event.data.testId" },
        Debounce = new Debounce(TimeSpan.FromSeconds(5)) { Key = "event.data.testId", Timeout = TimeSpan.FromMinutes(1) },
        Priority = new Priority("event.data.priority * 10"),
        Timeouts = new FunctionTimeouts { Start = TimeSpan.FromMinutes(10), Finish = TimeSpan.FromHours(1) },
        Singleton = new Singleton(SingletonMode.Skip) { Key = "event.data.testId" },
    };

    public override Task<object?> ExecuteAsync(FunctionContext<TestEvent> context, CancellationToken cancellationToken) =>
        Task.FromResult<object?>(null);
}

// Never triggered either. The server turns idempotency into a 24 hour rate limit keyed by the expression.
internal sealed class BatchedFunction : InngestFunction<TestEvent>
{
    public static FunctionOptions Options { get; } = new()
    {
        Id = "batched",
        Triggers = [Trigger.Event("itests/batched")],
        BatchEvents = new BatchEvents(10, TimeSpan.FromSeconds(5)) { Key = "event.data.testId" },
        Idempotency = "event.data.testId",
    };

    public override Task<object?> ExecuteAsync(FunctionContext<TestEvent> context, CancellationToken cancellationToken) =>
        Task.FromResult<object?>(null);
}
