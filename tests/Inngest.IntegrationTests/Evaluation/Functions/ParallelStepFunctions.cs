using Inngest.IntegrationTests.Evaluation.Harness;
using Microsoft.Extensions.DependencyInjection;

namespace Inngest.IntegrationTests.Evaluation.Functions;

internal sealed record FanOutResult(IReadOnlyList<int> Parts, int Total);

internal sealed record RaceResult(string Winner, string After);

internal sealed record ConfigureAwaitResult(int A, int B, IReadOnlyList<int> Parallel, string FromThreadPool);

internal sealed record BranchesResult(string Left, string Right);

// Steps started together and awaited with Task.WhenAll or Task.WhenAny, from code on and off the SDK's scheduler.
internal static class ParallelStepFunctions
{
    public const string FanOut = "eval/step.fan-out";
    public const string Race = "eval/step.race";
    public const string ConfigureAwaitFalse = "eval/step.configure-await";
    public const string Rounds = "eval/step.rounds";
    public const string Branches = "eval/step.branches";

    public const string SlowGate = "slow";

    public static void Add(IInngestBuilder inngest)
    {
        inngest.AddFunction<ValueEvent>(
            new FunctionOptions { Id = "step-fan-out", Triggers = [Trigger.Event(FanOut)] },
            static async (context, _) =>
            {
                var observed = context.Observed();
                var parts = await Task.WhenAll(Enumerable.Range(1, context.Event.Data.Value).Select(index =>
                    context.Step.RunAsync($"part-{index}", () =>
                    {
                        observed.Record("part", index);
                        return index * 10;
                    })));
                var total = await context.Step.RunAsync("total", () => parts.Sum());
                return new FanOutResult(parts, total);
            });

        // The fast step wins the race; the result must stay the same on every replay, after the slow step finished too.
        // The slow step waits at a gate until the scenario has seen the fast one finish.
        inngest.AddFunction<RunEvent>(
            new FunctionOptions { Id = "step-race", Triggers = [Trigger.Event(Race)] },
            static async (context, _) =>
            {
                var observed = context.Observed();
                var slow = context.Step.RunAsync("slow", async cancellationToken =>
                {
                    observed.Record("slow");
                    await context.Services.GetRequiredService<Gates>().WaitAsync(context.Event.Data.TestId, SlowGate, TimeSpan.FromSeconds(30), cancellationToken);
                    return "slow";
                });
                var fast = context.Step.RunAsync("fast", () =>
                {
                    observed.Record("fast");
                    return "fast";
                });

                var winner = await await Task.WhenAny(slow, fast);
                var after = await context.Step.RunAsync("after", () =>
                {
                    observed.Record("after", winner);
                    return $"{winner} won";
                });

                return new RaceResult(winner, after);
            });

        // Code that leaves the SDK's scheduler: ConfigureAwait(false), and a step called from a thread-pool thread.
        inngest.AddFunction<RunEvent>(
            new FunctionOptions { Id = "step-configure-await", Triggers = [Trigger.Event(ConfigureAwaitFalse)] },
            static async (context, cancellationToken) =>
            {
                var observed = context.Observed();
                var a = await context.Step.RunAsync("a", () =>
                {
                    observed.Record("a");
                    return 1;
                }).ConfigureAwait(false);

                await Task.Delay(50, cancellationToken).ConfigureAwait(false);
                var b = await Task.Run(
                    () => context.Step.RunAsync("b", () =>
                    {
                        observed.Record("b");
                        return a + 1;
                    }),
                    cancellationToken).ConfigureAwait(false);

                var parallel = await Task.WhenAll(
                    context.Step.RunAsync("c", () =>
                    {
                        observed.Record("c");
                        return b + 1;
                    }),
                    context.Step.RunAsync("d", () =>
                    {
                        observed.Record("d");
                        return b + 2;
                    })).ConfigureAwait(false);

                var fromThreadPool = await Task.Run(
                    async () => await context.Step.RunAsync("e", () =>
                    {
                        observed.Record("e");
                        return "from the thread pool";
                    }).ConfigureAwait(false),
                    cancellationToken).ConfigureAwait(false);

                return new ConfigureAwaitResult(a, b, parallel, fromThreadPool);
            });

        // Three rounds of three parallel steps, each round using the totals of the round before.
        inngest.AddFunction<RunEvent>(
            new FunctionOptions { Id = "step-rounds", Triggers = [Trigger.Event(Rounds)] },
            static async (context, _) =>
            {
                var observed = context.Observed();
                var totals = new List<int>();
                var carry = 1;
                for (var round = 1; round <= 3; round++)
                {
                    var current = round;
                    var input = carry;
                    var parts = await Task.WhenAll(Enumerable.Range(1, 3).Select(part =>
                        context.Step.RunAsync($"round-{current}-part-{part}", () =>
                        {
                            observed.Record("part", $"{current}.{part}");
                            return input * part;
                        })));
                    carry = await context.Step.RunAsync($"round-{current}-sum", () => parts.Sum());
                    totals.Add(carry);
                }

                return totals;
            });

        // Two branches running side by side, each with steps in sequence.
        inngest.AddFunction<RunEvent>(
            new FunctionOptions { Id = "step-branches", Triggers = [Trigger.Event(Branches)] },
            static async (context, _) =>
            {
                var observed = context.Observed();

                async Task<string> Branch(string name)
                {
                    var first = await context.Step.RunAsync($"{name}-1", () =>
                    {
                        observed.Record($"{name}-1");
                        return $"{name}1";
                    });
                    var second = await context.Step.RunAsync($"{name}-2", () =>
                    {
                        observed.Record($"{name}-2");
                        return $"{first}+{name}2";
                    });
                    return second;
                }

                var results = await Task.WhenAll(Branch("left"), Branch("right"));
                return new BranchesResult(results[0], results[1]);
            });
    }
}
