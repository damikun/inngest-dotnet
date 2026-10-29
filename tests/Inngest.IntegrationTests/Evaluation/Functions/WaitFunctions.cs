using Inngest.IntegrationTests.Evaluation.Harness;
using Microsoft.Extensions.DependencyInjection;

namespace Inngest.IntegrationTests.Evaluation.Functions;

internal sealed record SleepUntilEvent(string TestId, DateTimeOffset Until) : IScenarioEvent;

internal sealed record WaitStart(string TestId, string? OrderId = null, string? Group = null) : IScenarioEvent;

internal sealed record Resume(string TestId, string Message, string? OrderId = null, int Amount = 0, string? Group = null);

internal sealed record WaitOutcome(bool Matched, string? Message, string? EventId);

internal sealed record TwoWaits(string? First, string? Second);

internal sealed record Shipment(string TestId, string Carrier, IReadOnlyList<string> Parcels, decimal Weight);

internal sealed record TypedWait(string Name, string? Id, DateTimeOffset Timestamp, Shipment Data);

internal static class SleepFunctions
{
    public const string Past = "eval/sleep.past";
    public const string Parallel = "eval/sleep.parallel";
    public const string Loop = "eval/sleep.loop";
    public const string Until = "eval/sleep.until";
    public const string Zero = "eval/sleep.zero";

    public static void Add(IInngestBuilder inngest)
    {
        // The wake-up time comes from the event, so it is the same on every replay.
        inngest.AddFunction<SleepUntilEvent>(
            new FunctionOptions { Id = "sleep-past", Triggers = [Trigger.Event(Past)] },
            static async (context, _) =>
            {
                var observed = context.Observed();
                await context.Step.RunAsync("before", () => observed.Record("before"));
                await context.Step.SleepUntilAsync("until", context.Event.Data.Until);
                await context.Step.RunAsync("after", () => observed.Record("after"));
                return null;
            });

        inngest.AddFunction<RunEvent>(
            new FunctionOptions { Id = "sleep-parallel", Triggers = [Trigger.Event(Parallel)] },
            static async (context, _) =>
            {
                var observed = context.Observed();
                await context.Step.RunAsync("before", () => observed.Record("before"));
                await Task.WhenAll(
                    context.Step.SleepAsync("short", TimeSpan.FromSeconds(1)),
                    context.Step.SleepAsync("long", TimeSpan.FromSeconds(2)));
                await context.Step.RunAsync("after", () => observed.Record("after"));
                return null;
            });

        inngest.AddFunction<SleepUntilEvent>(
            new FunctionOptions { Id = "sleep-until", Triggers = [Trigger.Event(Until)] },
            static async (context, _) =>
            {
                var observed = context.Observed();
                await context.Step.SleepUntilAsync("until", context.Event.Data.Until);
                await context.Step.RunAsync("woke", () => observed.Record("woke"));
                return null;
            });

        inngest.AddFunction<RunEvent>(
            new FunctionOptions { Id = "sleep-zero", Triggers = [Trigger.Event(Zero)] },
            static async (context, _) =>
            {
                var observed = context.Observed();
                await context.Step.RunAsync("before", () => observed.Record("before"));
                await context.Step.SleepAsync("no-time", TimeSpan.Zero);
                await context.Step.RunAsync("after", () => observed.Record("after"));
                return null;
            });

        // The same sleep id in every iteration.
        inngest.AddFunction<RunEvent>(
            new FunctionOptions { Id = "sleep-loop", Triggers = [Trigger.Event(Loop)] },
            static async (context, _) =>
            {
                var observed = context.Observed();
                for (var i = 0; i < 3; i++)
                {
                    var tick = i;
                    await context.Step.RunAsync("tick", () => observed.Record("tick", tick));
                    await context.Step.SleepAsync("nap", TimeSpan.FromSeconds(1));
                }

                return "rested";
            });
    }
}

internal static class WaitForEventFunctions
{
    public const string MatchStart = "eval/wait.match.start";
    public const string MatchResume = "eval/wait.match.resume";
    public const string IfStart = "eval/wait.if.start";
    public const string IfResume = "eval/wait.if.resume";
    public const string TimeoutStart = "eval/wait.timeout.start";
    public const string TimeoutResume = "eval/wait.timeout.resume";
    public const string EarlyStart = "eval/wait.early.start";
    public const string EarlyResume = "eval/wait.early.resume";
    public const string ParallelStart = "eval/wait.parallel.start";
    public const string ParallelFirst = "eval/wait.parallel.first";
    public const string ParallelSecond = "eval/wait.parallel.second";
    public const string MixedStart = "eval/wait.mixed.start";
    public const string MixedNever = "eval/wait.mixed.never";
    public const string MixedArrives = "eval/wait.mixed.arrives";
    public const string SharedStart = "eval/wait.shared.start";
    public const string SharedGo = "eval/wait.shared.go";
    public const string TypedStart = "eval/wait.typed.start";
    public const string TypedResume = "eval/wait.typed.resume";

    public const string PreparedGate = "prepared";

    public static readonly TimeSpan ShortTimeout = TimeSpan.FromSeconds(1);

    // Long enough for the scenario to see both waits stored before this one times out.
    public static readonly TimeSpan MixedTimeout = TimeSpan.FromSeconds(3);

    private static readonly TimeSpan LongTimeout = TimeSpan.FromSeconds(30);

    public static void Add(IInngestBuilder inngest)
    {
        inngest.AddFunction<WaitStart>(
            new FunctionOptions { Id = "wait-match", Triggers = [Trigger.Event(MatchStart)] },
            static async (context, _) =>
                Outcome(await context.Step.WaitForEventAsync<Resume>("resume", new WaitForEventOptions(MatchResume, LongTimeout) { Match = "data.orderId" })));

        inngest.AddFunction<WaitStart>(
            new FunctionOptions { Id = "wait-if", Triggers = [Trigger.Event(IfStart)] },
            static async (context, _) =>
                Outcome(await context.Step.WaitForEventAsync<Resume>(
                    "big-payment",
                    new WaitForEventOptions(IfResume, LongTimeout) { If = "async.data.testId == event.data.testId && async.data.amount > 100" })));

        inngest.AddFunction<WaitStart>(
            new FunctionOptions { Id = "wait-timeout", Triggers = [Trigger.Event(TimeoutStart)] },
            static async (context, _) =>
            {
                var observed = context.Observed();
                await context.Step.RunAsync("before", () => observed.Record("before"));
                var resumed = await context.Step.WaitForEventAsync<Resume>("resume", new WaitForEventOptions(TimeoutResume, ShortTimeout) { Match = "data.testId" });
                await context.Step.RunAsync("after", () => observed.Record("after"));
                return Outcome(resumed);
            });

        // The scenario sends the awaited event while "prepare" is held at the gate, before the wait exists.
        inngest.AddFunction<WaitStart>(
            new FunctionOptions { Id = "wait-early", Triggers = [Trigger.Event(EarlyStart)] },
            static async (context, _) =>
            {
                var observed = context.Observed();
                await context.Step.RunAsync("prepare", async cancellationToken =>
                {
                    observed.Record("prepare");
                    await context.Services.GetRequiredService<Gates>().WaitAsync(context.Event.Data.TestId, PreparedGate, TimeSpan.FromSeconds(20), cancellationToken);
                });

                return Outcome(await context.Step.WaitForEventAsync<Resume>("resume", new WaitForEventOptions(EarlyResume, LongTimeout) { Match = "data.testId" }));
            });

        inngest.AddFunction<WaitStart>(
            new FunctionOptions { Id = "wait-parallel", Triggers = [Trigger.Event(ParallelStart)] },
            static async (context, _) =>
            {
                var waits = await Task.WhenAll(
                    context.Step.WaitForEventAsync<Resume>("first", new WaitForEventOptions(ParallelFirst, LongTimeout) { Match = "data.testId" }),
                    context.Step.WaitForEventAsync<Resume>("second", new WaitForEventOptions(ParallelSecond, LongTimeout) { Match = "data.testId" }));
                return new TwoWaits(waits[0]?.Data.Message, waits[1]?.Data.Message);
            });

        inngest.AddFunction<WaitStart>(
            new FunctionOptions { Id = "wait-mixed", Triggers = [Trigger.Event(MixedStart)] },
            static async (context, _) =>
            {
                var waits = await Task.WhenAll(
                    context.Step.WaitForEventAsync<Resume>("never", new WaitForEventOptions(MixedNever, MixedTimeout) { Match = "data.testId" }),
                    context.Step.WaitForEventAsync<Resume>("arrives", new WaitForEventOptions(MixedArrives, LongTimeout) { Match = "data.testId" }));
                return new TwoWaits(waits[0]?.Data.Message, waits[1]?.Data.Message);
            });

        inngest.AddFunction<WaitStart>(
            new FunctionOptions { Id = "wait-shared", Triggers = [Trigger.Event(SharedStart)] },
            static async (context, _) =>
                Outcome(await context.Step.WaitForEventAsync<Resume>("go", new WaitForEventOptions(SharedGo, LongTimeout) { If = "async.data.group == event.data.group" })));

        inngest.AddFunction<WaitStart>(
            new FunctionOptions { Id = "wait-typed", Triggers = [Trigger.Event(TypedStart)] },
            static async (context, _) =>
            {
                var shipment = await context.Step.WaitForEventAsync<Shipment>("shipment", new WaitForEventOptions(TypedResume, LongTimeout) { Match = "data.testId" });
                return shipment is null ? null : new TypedWait(shipment.Name, shipment.Id, shipment.Timestamp, shipment.Data);
            });
    }

    private static WaitOutcome Outcome(InngestEvent<Resume>? resumed) => new(resumed is not null, resumed?.Data.Message, resumed?.Id);
}
