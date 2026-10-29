using Inngest.IntegrationTests.Evaluation.Functions;
using Inngest.IntegrationTests.Harness;

namespace Inngest.IntegrationTests.Checkpointing;

internal sealed record StepsResult(IReadOnlyList<string> Steps);

internal sealed record AttemptsResult(string A, string B, int BAttempt, string C, int CAttempt);

internal sealed record StepAttempt(string Step, int Attempt);

// Each scenario has a function of its own, so the calls the app received for that function are the calls of one run.
internal static class CheckpointingFunctions
{
    public const string Chain = "ckpt/chain";
    public const string ClassicChain = "ckpt/classic-chain";
    public const string Sleep = "ckpt/sleep";
    public const string Retry = "ckpt/retry";
    public const string Parallel = "ckpt/parallel";

    public static readonly TimeSpan Nap = TimeSpan.FromSeconds(2);

    public static void Add(IInngestBuilder inngest)
    {
        // Checkpointing comes from the app's setting.
        inngest.AddFunction<ValueEvent>(
            new FunctionOptions { Id = "ckpt-chain", Triggers = [Trigger.Event(Chain)] },
            static (context, _) => ChainAsync(context));

        inngest.AddFunction<ValueEvent>(
            new FunctionOptions
            {
                Id = "ckpt-classic-chain",
                Triggers = [Trigger.Event(ClassicChain)],
                Checkpointing = new CheckpointingOptions { Enabled = false },
            },
            static (context, _) => ChainAsync(context));

        inngest.AddFunction<RunEvent>(
            new FunctionOptions { Id = "ckpt-sleep", Triggers = [Trigger.Event(Sleep)] },
            static async (context, _) =>
            {
                var observed = context.Observed();
                observed.Record("call");
                var a = await context.Step.RunAsync("a", () => Ran(observed, "a"));
                await context.Step.SleepAsync("nap", Nap);
                var b = await context.Step.RunAsync("b", () => Ran(observed, "b"));
                return new StepsResult([a, b]);
            });

        // The second step fails on its first attempt. Inngest retries it in a call of its own, which answers right after
        // the step, so the third step starts again from attempt 0.
        inngest.AddFunction<RunEvent>(
            new FunctionOptions { Id = "ckpt-retry", Retries = 2, Triggers = [Trigger.Event(Retry)] },
            static async (context, _) =>
            {
                var observed = context.Observed();
                observed.Record("call");
                var a = await context.Step.RunAsync("a", () => Ran(observed, "a"));
                var b = await context.Step.RunAsync("b", () =>
                {
                    Ran(observed, "b");
                    return context.Attempt == 0
                        ? throw new InvalidOperationException("the first attempt fails")
                        : new StepAttempt("b", context.Attempt);
                });
                var c = await context.Step.RunAsync("c", () => new StepAttempt(Ran(observed, "c"), context.Attempt));
                return new AttemptsResult(a, b.Step, b.Attempt, c.Step, c.Attempt);
            });

        inngest.AddFunction<RunEvent>(
            new FunctionOptions { Id = "ckpt-parallel", Triggers = [Trigger.Event(Parallel)] },
            static async (context, _) =>
            {
                var observed = context.Observed();
                observed.Record("call");
                var a = await context.Step.RunAsync("a", () => Ran(observed, "a"));
                var parts = await Task.WhenAll(
                    context.Step.RunAsync("b", () => Ran(observed, "b")),
                    context.Step.RunAsync("c", () => Ran(observed, "c")));
                var d = await context.Step.RunAsync("d", () => Ran(observed, "d"));
                return new StepsResult([a, .. parts, d]);
            });
    }

    // Code outside the steps runs once per call, so "call" counts the calls of the run.
    private static async Task<object?> ChainAsync(FunctionContext<ValueEvent> context)
    {
        var observed = context.Observed();
        observed.Record("call");
        var steps = new List<string>();
        for (var i = 1; i <= context.Event.Data.Value; i++)
        {
            var id = $"step-{i}";
            steps.Add(await context.Step.RunAsync(id, () => Ran(observed, id)));
        }

        return new StepsResult(steps);
    }

    private static string Ran(TestRecording observed, string step)
    {
        observed.Record(step);
        return step;
    }
}
