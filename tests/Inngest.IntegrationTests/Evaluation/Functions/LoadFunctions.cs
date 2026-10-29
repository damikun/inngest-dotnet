using System.Text;
using Inngest.IntegrationTests.Evaluation.Harness;

namespace Inngest.IntegrationTests.Evaluation.Functions;

internal sealed record BigStateResult(IReadOnlyList<int> Lengths, IReadOnlyList<string> Hashes);

internal sealed record BlobEvent(string TestId, int Index, string Blob) : IScenarioEvent;

internal sealed record BatchSummary(int Count, IReadOnlyList<int> Indexes, long TotalLength, string Sha256);

internal static class LoadFunctions
{
    public const string Runs = "eval/load.runs";
    public const string Sleepers = "eval/load.sleepers";

    public static readonly TimeSpan Nap = TimeSpan.FromSeconds(2);

    public static void Add(IInngestBuilder inngest)
    {
        inngest.AddFunction<ValueEvent>(
            new FunctionOptions { Id = "load-runs", Triggers = [Trigger.Event(Runs)] },
            static async (context, _) =>
            {
                var observed = context.Observed();
                var value = context.Event.Data.Value;
                var doubled = await context.Step.RunAsync("work", async cancellationToken =>
                {
                    await ConcurrencyFunctions.TimedAsync(observed, "work", TimeSpan.FromMilliseconds(200), cancellationToken);
                    return value * 2;
                });
                return await context.Step.RunAsync("finish", () =>
                {
                    observed.Record("done", value);
                    return doubled + 1;
                });
            });

        inngest.AddFunction<ValueEvent>(
            new FunctionOptions { Id = "load-sleepers", Triggers = [Trigger.Event(Sleepers)] },
            static async (context, _) =>
            {
                var observed = context.Observed();
                var value = context.Event.Data.Value;
                var before = await context.Step.RunAsync("before", () => DateTimeOffset.UtcNow);
                await context.Step.SleepAsync("nap", Nap);
                await context.Step.RunAsync("after", () => observed.Record("slept", new Interval(before, DateTimeOffset.UtcNow)));
                return value;
            });
    }
}

// Runs that make Inngest send its state through the API instead of in the call (use_api), and a very long run.
internal static class LargeStateFunctions
{
    public const string BigState = "eval/load.big-state";
    public const string ManySteps = "eval/load.many-steps";
    public const string BigBatch = "eval/load.big-batch";
    public const string BigParallel = "eval/load.big-parallel";

    // Three of these pass the 4 MB that Inngest puts in a call.
    public const int ChunkLength = 1_500_000;

    public static void Add(IInngestBuilder inngest)
    {
        inngest.AddFunction<RunEvent>(
            new FunctionOptions { Id = "load-big-state", Triggers = [Trigger.Event(BigState)] },
            static async (context, _) =>
            {
                var observed = context.Observed();
                var testId = context.Event.Data.TestId;
                var chunks = new List<string>();
                for (var i = 1; i <= 3; i++)
                {
                    var index = i;
                    chunks.Add(await context.Step.RunAsync($"chunk-{index}", () =>
                    {
                        observed.Record("chunk", index);
                        return StepDataFunctions.LargeText($"{testId}-{index}", ChunkLength);
                    }));
                }

                return await context.Step.RunAsync("check", () =>
                    new BigStateResult([.. chunks.Select(chunk => chunk.Length)], [.. chunks.Select(StepDataFunctions.Sha256)]));
            });

        // Past 4 MB, steps that run in parallel each get a call of their own that targets them, also with use_api.
        inngest.AddFunction<RunEvent>(
            new FunctionOptions { Id = "load-big-parallel", Triggers = [Trigger.Event(BigParallel)] },
            static async (context, _) =>
            {
                var observed = context.Observed();
                var testId = context.Event.Data.TestId;
                var lengths = 0;
                for (var i = 1; i <= 3; i++)
                {
                    var index = i;
                    lengths += (await context.Step.RunAsync($"chunk-{index}", () => StepDataFunctions.LargeText($"{testId}-{index}", ChunkLength))).Length;
                }

                var parts = await Task.WhenAll(Enumerable.Range(1, 3).Select(part => context.Step.RunAsync($"part-{part}", () =>
                {
                    observed.Record("part", part);
                    return part * 10;
                })));
                return new { lengths, parts };
            });

        // Every call replays all steps before it, so this also measures how replay cost grows with a run.
        inngest.AddFunction<ValueEvent>(
            new FunctionOptions { Id = "load-many-steps", Triggers = [Trigger.Event(ManySteps)] },
            static async (context, _) =>
            {
                var observed = context.Observed();
                var sum = 0L;
                for (var i = 1; i <= context.Event.Data.Value; i++)
                {
                    var index = i;
                    sum += await context.Step.RunAsync($"step-{index}", () =>
                    {
                        observed.Record("step", index);
                        return index;
                    });
                }

                return new ChainResult(context.Event.Data.Value, (int)sum);
            });

        inngest.AddFunction<BlobEvent>(
            new FunctionOptions
            {
                Id = "load-big-batch",
                Triggers = [Trigger.Event(BigBatch)],
                BatchEvents = new BatchEvents(10, TimeSpan.FromSeconds(5)) { Key = "event.data.testId" },
            },
            static (context, _) =>
            {
                var events = context.Events.OrderBy(@event => @event.Data.Index).ToList();
                var all = new StringBuilder();
                foreach (var @event in events)
                {
                    all.Append(@event.Data.Blob);
                }

                context.Observed().Record("batch", events.Count);
                return Task.FromResult<object?>(new BatchSummary(
                    events.Count,
                    [.. events.Select(@event => @event.Data.Index)],
                    all.Length,
                    StepDataFunctions.Sha256(all.ToString())));
            });
    }
}
