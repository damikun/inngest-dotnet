using Inngest.IntegrationTests.Evaluation.Functions;
using Inngest.IntegrationTests.Evaluation.Harness;
using Microsoft.Extensions.DependencyInjection;

namespace Inngest.IntegrationTests.Steps;

internal sealed record Verdict(string TestId, string By);

// Races of steps, in race mode and, for comparison, without it.
internal static class RaceFunctions
{
    public const string Waits = "steps/race.waits";
    public const string WaitsWithoutRaceMode = "steps/race.waits-without-race-mode";
    public const string WaitsThenSteps = "steps/race.waits-then-steps";
    public const string Approved = "steps/race.approved";
    public const string Rejected = "steps/race.rejected";
    public const string Runs = "steps/race.runs";
    public const string Branches = "steps/race.branches";
    public const string Sleep = "steps/race.sleep";

    public const string SlowGate = "slow";

    // How long a wait that loses a race waits. A run that only goes on once this has passed did not race.
    public static readonly TimeSpan LoserTimeout = TimeSpan.FromSeconds(8);

    private static readonly TimeSpan GateTimeout = TimeSpan.FromSeconds(30);

    public static void Add(IInngestBuilder inngest)
    {
        inngest.AddFunction<RunEvent>(
            new FunctionOptions { Id = "race-waits", Triggers = [Trigger.Event(Waits)] },
            static (context, _) => DecideAsync(context, raceMode: true));

        inngest.AddFunction<RunEvent>(
            new FunctionOptions { Id = "race-waits-without-race-mode", Triggers = [Trigger.Event(WaitsWithoutRaceMode)] },
            static (context, _) => DecideAsync(context, raceMode: false));

        // The wait that lost is still open after the race, and Inngest waits for it before it goes past a step that
        // is not in race mode. Steps in race mode after the race go ahead one after another.
        inngest.AddFunction<RunEvent>(
            new FunctionOptions { Id = "race-waits-then-steps", Triggers = [Trigger.Event(WaitsThenSteps)] },
            static async (context, _) =>
            {
                var observed = context.Observed();
                await context.Step.RunAsync("prepare", () => observed.Record("waiting"));
                return await context.Step.ParallelAsync(ParallelMode.Race, async () =>
                {
                    var verdict = await await Task.WhenAny(
                        context.Step.WaitForEventAsync<Verdict>("approved", Wait(Approved)),
                        context.Step.WaitForEventAsync<Verdict>("rejected", Wait(Rejected)));
                    await context.Step.RunAsync("notify", () => observed.Record("notified"));
                    return await context.Step.RunAsync("archive", () =>
                    {
                        observed.Record("archived");
                        return verdict?.Name;
                    });
                });
            });

        // The slow step waits at a gate until the scenario has seen the step after the race run.
        inngest.AddFunction<RunEvent>(
            new FunctionOptions { Id = "race-runs", Triggers = [Trigger.Event(Runs)] },
            static async (context, _) =>
            {
                var observed = context.Observed();
                var gates = context.Services.GetRequiredService<Gates>();
                var winner = await await context.Step.ParallelAsync(ParallelMode.Race, () => Task.WhenAny(
                    context.Step.RunAsync("slow", async cancellationToken =>
                    {
                        observed.Record("slow");
                        await gates.WaitAsync(context.Event.Data.TestId, SlowGate, GateTimeout, cancellationToken);
                        return "slow";
                    }),
                    context.Step.RunAsync("fast", () =>
                    {
                        observed.Record("fast");
                        return "fast";
                    })));

                return await context.Step.RunAsync("after", () =>
                {
                    observed.Record("after", winner);
                    return winner;
                });
            });

        // The TypeScript SDK's test of race mode: A then B go ahead while C runs. After B, C is the only step left
        // without a result, and the SDK must plan it again rather than run it a second time.
        inngest.AddFunction<RunEvent>(
            new FunctionOptions { Id = "race-branches", Triggers = [Trigger.Event(Branches)] },
            static async (context, _) =>
            {
                var observed = context.Observed();
                var gates = context.Services.GetRequiredService<Gates>();

                async Task<string> Branch()
                {
                    var a = await context.Step.RunAsync("A", () => observed.Record("A"));
                    return await context.Step.RunAsync("B", () => $"{a}+{observed.Record("B")}");
                }

                var results = await context.Step.ParallelAsync(ParallelMode.Race, () => Task.WhenAll(
                    Branch(),
                    context.Step.RunAsync("C", async cancellationToken =>
                    {
                        observed.Record("C");
                        await gates.WaitAsync(context.Event.Data.TestId, SlowGate, GateTimeout, cancellationToken);
                        return "C";
                    })));

                return await context.Step.RunAsync("Final", () =>
                {
                    observed.Record("Final");
                    return string.Join(",", results);
                });
            });

        // A sleep that cuts a long wait short, as an escalation would.
        inngest.AddFunction<RunEvent>(
            new FunctionOptions { Id = "race-sleep", Triggers = [Trigger.Event(Sleep)] },
            static async (context, _) =>
            {
                var observed = context.Observed();
                await context.Step.RunAsync("prepare", () => observed.Record("waiting"));

                Task? approval = null;
                var first = await context.Step.ParallelAsync(ParallelMode.Race, () =>
                {
                    approval = context.Step.WaitForEventAsync<Verdict>("approved", Wait(Approved));
                    return Task.WhenAny(approval, context.Step.SleepAsync("escalate-after", TimeSpan.FromSeconds(1)));
                });

                var outcome = first == approval ? "approved" : "escalated";
                return await context.Step.RunAsync("outcome", () =>
                {
                    observed.Record("outcome", outcome);
                    return outcome;
                });
            });
    }

    // Approved or rejected, whichever comes first.
    private static async Task<object?> DecideAsync(FunctionContext<RunEvent> context, bool raceMode)
    {
        var observed = context.Observed();
        await context.Step.RunAsync("prepare", () => observed.Record("waiting"));

        Task<Task<InngestEvent<Verdict>?>> Race() => Task.WhenAny(
            context.Step.WaitForEventAsync<Verdict>("approved", Wait(Approved)),
            context.Step.WaitForEventAsync<Verdict>("rejected", Wait(Rejected)));

        var first = await (raceMode ? context.Step.ParallelAsync(ParallelMode.Race, Race) : Race());
        var verdict = await first;
        return await context.Step.RunAsync("decide", () =>
        {
            observed.Record("decided", verdict?.Name);
            return verdict?.Name ?? "nobody";
        });
    }

    private static WaitForEventOptions Wait(string name) => new(name, LoserTimeout) { Match = "data.testId" };
}

// Steps with display names, as the server shows them.
internal static class NamedStepFunctions
{
    public const string Named = "steps/names";

    public static void Add(IInngestBuilder inngest) =>
        inngest.AddFunction<RunEvent>(
            new FunctionOptions { Id = "named-steps", Triggers = [Trigger.Event(Named)] },
            static async (context, _) =>
            {
                var observed = context.Observed();
                var receipt = await context.Step.RunAsync(new StepOptions("charge") { Name = "Charge the card" }, () => "R-1");
                await context.Step.SleepAsync(new StepOptions("cool-off") { Name = "Cool off" }, TimeSpan.FromSeconds(1));
                await context.Step.RunAsync("ship", () => observed.Record("shipped"));
                return receipt;
            });
}
