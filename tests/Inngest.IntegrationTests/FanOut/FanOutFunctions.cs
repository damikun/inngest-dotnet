using System.Collections.Concurrent;
using Inngest.IntegrationTests.Evaluation.Functions;
using Inngest.IntegrationTests.Evaluation.Harness;
using Inngest.IntegrationTests.Harness;
using Microsoft.Extensions.DependencyInjection;

namespace Inngest.IntegrationTests.FanOut;

internal sealed record ChunkedEvent(string TestId, int Count, int ChunkSize, int? FailAt) : IScenarioEvent;

// An item of a list that child runs process. It carries the test id so that the function of a chunk, which has no
// event of the test, can record what it saw.
internal sealed record ChunkItem(string TestId, int Number, bool Fail);

internal sealed record ChunkResult(int Number, int Square);

// How many handlers of a test run at this moment.
internal sealed class InFlight
{
    private readonly ConcurrentDictionary<string, int> _counts = new(StringComparer.Ordinal);

    public int Enter(string testId) => _counts.AddOrUpdate(testId, 1, static (_, count) => count + 1);

    public void Leave(string testId) => _counts.AddOrUpdate(testId, 0, static (_, count) => count - 1);
}

// Holds the handlers of a test at a point until as many as it expects have arrived there, so that a test that needs the
// items of a window to overlap does not rely on how long a handler takes.
internal sealed class Rendezvous
{
    private readonly ConcurrentDictionary<string, Point> _points = new(StringComparer.Ordinal);

    // Returns once `expected` handlers have arrived at the point, and throws when that takes longer than the timeout.
    public async Task ArriveAsync(string testId, string name, int expected, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var point = _points.GetOrAdd($"{testId}/{name}", static _ => new Point());
        if (Interlocked.Increment(ref point.Arrived) >= expected)
        {
            point.Reached.TrySetResult();
        }

        try
        {
            await point.Reached.Task.WaitAsync(timeout, cancellationToken);
        }
        catch (TimeoutException)
        {
            throw new InvalidOperationException($"{expected} handlers did not arrive at {name} of test {testId} within {timeout}.");
        }
    }

    private sealed class Point
    {
        public readonly TaskCompletionSource Reached = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Arrived;
    }
}

// The function of a chunk: two items at a time, each a step of the chunk's run.
internal sealed class SquareChunk(Probe probe) : FanOutFunction<ChunkItem, ChunkResult>
{
    protected override int? MaxParallelism => 2;

    protected override Task<ChunkResult> ProcessAsync(ChunkItem item, CancellationToken cancellationToken)
    {
        probe.For(item.TestId).Record("chunk-item", item.Number);
        return item.Fail
            ? throw new NonRetriableException($"Item {item.Number} was refused")
            : Task.FromResult(new ChunkResult(item.Number, item.Number * item.Number));
    }
}

internal static class FanOutFunctions
{
    public const string Window = "fanout/window";
    public const string FailFast = "fanout/fail-fast";
    public const string Outcomes = "fanout/outcomes";
    public const string Race = "fanout/race";
    public const string Waves = "fanout/waves";
    public const string RaceFailure = "fanout/race-failure";
    public const string Limit = "fanout/limit";
    public const string Chunks = "fanout/chunks";
    public const string ChunkOutcomes = "fanout/chunk-outcomes";

    public const string SlowGate = "slow";

    private static readonly TimeSpan GateTimeout = TimeSpan.FromSeconds(30);

    public static void Add(IInngestBuilder inngest)
    {
        // Seven items, three at a time: the windows are 0 1 2, 3 4 5 and 6. The items of a window wait for each other, so
        // that they are in flight together whatever the machine is doing.
        inngest.AddFunction<ValueEvent>(
            new FunctionOptions { Id = "fanout-window", Triggers = [Trigger.Event(Window)] },
            static async (context, _) =>
            {
                var observed = context.Observed();
                var inFlight = context.Services.GetRequiredService<InFlight>();
                var rendezvous = context.Services.GetRequiredService<Rendezvous>();
                var testId = context.Event.Data.TestId;
                var count = context.Event.Data.Value;
                return await context.Step.MapAsync(
                    "square",
                    Enumerable.Range(0, count).ToArray(),
                    async (item, cancellationToken) =>
                    {
                        observed.Record("running", inFlight.Enter(testId));
                        try
                        {
                            var window = item / 3;
                            await rendezvous.ArriveAsync(testId, $"window-{window}", Math.Min(3, count - (window * 3)), GateTimeout, cancellationToken);
                            observed.Record("item", item);
                            return item * item;
                        }
                        finally
                        {
                            inFlight.Leave(testId);
                        }
                    },
                    new FanOutOptions { MaxParallelism = 3 });
            });

        // Item 1 is refused for good. Items after the window that holds it must not start.
        inngest.AddFunction<ValueEvent>(
            new FunctionOptions { Id = "fanout-fail-fast", Triggers = [Trigger.Event(FailFast)] },
            static async (context, _) =>
            {
                var observed = context.Observed();
                return await context.Step.MapAsync(
                    "charge",
                    Enumerable.Range(0, context.Event.Data.Value).ToArray(),
                    (item, _) =>
                    {
                        observed.Record("item", item);
                        return item == 1 ? throw new NonRetriableException("Card declined for item 1") : Task.FromResult(item);
                    },
                    new FanOutOptions { MaxParallelism = 2 });
            });

        // Items 1 and 3 are refused for good, and the others still run.
        inngest.AddFunction<ValueEvent>(
            new FunctionOptions { Id = "fanout-outcomes", Triggers = [Trigger.Event(Outcomes)] },
            static async (context, _) =>
            {
                var observed = context.Observed();
                return await context.Step.MapOutcomesAsync(
                    "charge",
                    Enumerable.Range(0, context.Event.Data.Value).ToArray(),
                    (item, _) =>
                    {
                        observed.Record("item", item);
                        return item is 1 or 3 ? throw new NonRetriableException($"Card declined for item {item}") : Task.FromResult(item * 10);
                    },
                    new FanOutOptions { MaxParallelism = 2 });
            });

        inngest.AddFunction<RunEvent>(
            new FunctionOptions { Id = "fanout-race", Triggers = [Trigger.Event(Race)] },
            static (context, _) => GatedAsync(context, ParallelMode.Race));

        inngest.AddFunction<RunEvent>(
            new FunctionOptions { Id = "fanout-waves", Triggers = [Trigger.Event(Waves)] },
            static (context, _) => GatedAsync(context, null));

        // As many items as the event says, to try the number of steps a run can hold. The function records each call.
        inngest.AddFunction<ValueEvent>(
            new FunctionOptions { Id = "fanout-limit", Triggers = [Trigger.Event(Limit)] },
            static async (context, _) =>
            {
                var observed = context.Observed();
                observed.Record("call");
                var results = await context.Step.MapAsync(
                    "item",
                    Enumerable.Range(0, context.Event.Data.Value).ToArray(),
                    (item, _) =>
                    {
                        observed.Record("item", item);
                        return Task.FromResult(item);
                    },
                    new FanOutOptions { MaxParallelism = 250 });
                return results.Length;
            });

        // Item 0 is refused for good while item 1 waits at a gate. In race mode Inngest calls the function after item 0
        // failed, and the run fails without waiting for item 1.
        inngest.AddFunction<RunEvent>(
            new FunctionOptions { Id = "fanout-race-failure", Triggers = [Trigger.Event(RaceFailure)] },
            static async (context, _) =>
            {
                var observed = context.Observed();
                var gates = context.Services.GetRequiredService<Gates>();
                return await context.Step.MapAsync(
                    "race",
                    new[] { 0, 1, 2, 3 },
                    async (item, cancellationToken) =>
                    {
                        observed.Record("start", item);
                        if (item == 0)
                        {
                            throw new NonRetriableException("Card declined for item 0");
                        }

                        if (item == 1)
                        {
                            await gates.WaitAsync(context.Event.Data.TestId, SlowGate, GateTimeout, cancellationToken);
                        }

                        return item;
                    },
                    new FanOutOptions { MaxParallelism = 2, ParallelMode = ParallelMode.Race });
            });

        inngest.AddFunction<ChunkedEvent>(
            new FunctionOptions { Id = "fanout-chunks", Triggers = [Trigger.Event(Chunks)] },
            static async (context, _) => await context.Step.MapInChunksAsync<SquareChunk, ChunkItem, ChunkResult>(
                "square",
                ItemsOf(context.Event.Data),
                context.Event.Data.ChunkSize,
                new FanOutOptions { MaxParallelism = context.Event.Data.FailAt is null ? 2 : 1 }));

        inngest.AddFunction<ChunkedEvent>(
            new FunctionOptions { Id = "fanout-chunk-outcomes", Triggers = [Trigger.Event(ChunkOutcomes)] },
            static async (context, _) => await context.Step.MapOutcomesInChunksAsync<SquareChunk, ChunkItem, ChunkResult>(
                "square",
                ItemsOf(context.Event.Data),
                context.Event.Data.ChunkSize,
                new FanOutOptions { MaxParallelism = 2 }));

        inngest.AddFunction<SquareChunk>(new FunctionOptions { Id = "fanout-square-chunk", Triggers = [], Retries = 0 });
    }

    private static ChunkItem[] ItemsOf(ChunkedEvent data) =>
        [.. Enumerable.Range(0, data.Count).Select(number => new ChunkItem(data.TestId, number, number == data.FailAt))];

    // Four items, two at a time. Item 0 waits at a gate, so the others show whether the window moves on without it.
    private static async Task<object?> GatedAsync(FunctionContext<RunEvent> context, ParallelMode? mode)
    {
        var observed = context.Observed();
        var gates = context.Services.GetRequiredService<Gates>();
        return await context.Step.MapAsync(
            "gated",
            new[] { 0, 1, 2, 3 },
            async (item, cancellationToken) =>
            {
                observed.Record("start", item);
                if (item == 0)
                {
                    await gates.WaitAsync(context.Event.Data.TestId, SlowGate, GateTimeout, cancellationToken);
                }

                observed.Record("item", item);
                return item * 10;
            },
            new FanOutOptions { MaxParallelism = 2, ParallelMode = mode });
    }
}
