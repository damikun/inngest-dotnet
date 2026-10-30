using Inngest.Execution;
using Inngest.Testing;
using Inngest.Tests.Testing;
using static Inngest.Tests.Testing.TestHosts;

namespace Inngest.Tests.FanOut;

// Functions that fan out, run by the test host with the per-item steps mocked or really run.
public sealed class FanOutTestHostTests
{
    private static readonly string[] Letters = ["a", "b", "c", "d", "e"];

    [Fact]
    public async Task The_steps_of_the_items_are_mocked_by_their_ids()
    {
        await using var host = For(async context =>
            (object?)await context.Step.MapAsync("charge", Letters, (item, _) => Task.FromResult(item), new FanOutOptions { MaxParallelism = 2 }));
        var mocks = new StepMocks();
        for (var i = 0; i < Letters.Length; i++)
        {
            mocks = mocks.Run($"charge[{i}]", Letters[i].ToUpperInvariant());
        }

        var run = await host.RunAsync("fn", Event(), mocks, Cancel);

        run.Status.ShouldBe(TestRunStatus.Completed);
        run.GetOutput<string[]>().ShouldBe(["A", "B", "C", "D", "E"]);
        run.Steps.Select(step => step.Id).ShouldBe(["charge[0]", "charge[1]", "charge[2]", "charge[3]", "charge[4]"]);
        run.Steps.ShouldAllBe(step => step.Mocked);
    }

    [Fact]
    public async Task A_list_costs_one_call_per_item_and_one_per_window_plus_the_last_one()
    {
        RecordingEngine engine = null!;
        await using var host = new InngestTestHost(
            Services(async context =>
                (object?)await context.Step.MapAsync("charge", Letters, (item, _) => Task.FromResult(item), new FanOutOptions { MaxParallelism = 2 })),
            inner => engine = new RecordingEngine(inner));

        var run = await host.RunAsync("fn", Event(), Cancel);

        run.GetOutput<string[]>().ShouldBe(Letters);

        // Windows of 0 1, 2 3 and 4: three calls that find steps, one that runs the last item in the call that found it,
        // four calls that run an item each, and the call that returns.
        engine.Targets.ShouldBe(
            [
                "step", Hash("charge[0]"), Hash("charge[1]"),
                "step", Hash("charge[2]"), Hash("charge[3]"),
                "step",
                "step",
            ]);
    }

    [Fact]
    public async Task Without_a_limit_a_list_costs_one_call_per_item_and_two_more()
    {
        RecordingEngine engine = null!;
        await using var host = new InngestTestHost(
            Services(async context => (object?)await context.Step.MapAsync("charge", Letters, (item, _) => Task.FromResult(item))),
            inner => engine = new RecordingEngine(inner));

        await host.RunAsync("fn", Event(), Cancel);

        engine.Targets.Count.ShouldBe(Letters.Length + 2);
    }

    [Fact]
    public async Task In_race_mode_the_last_item_does_not_run_in_the_call_that_finds_it()
    {
        RecordingEngine engine = null!;
        await using var host = new InngestTestHost(
            Services(async context =>
                (object?)await context.Step.MapAsync(
                    "charge",
                    Letters,
                    (item, _) => Task.FromResult(item),
                    new FanOutOptions { MaxParallelism = 2, ParallelMode = ParallelMode.Race })),
            inner => engine = new RecordingEngine(inner));

        var run = await host.RunAsync("fn", Event(), Cancel);

        run.GetOutput<string[]>().ShouldBe(Letters);
        run.Steps.Count.ShouldBe(Letters.Length);

        // A step of a race is always planned and run in a call of its own: once the others finished, Inngest may be
        // running it already.
        engine.Targets.ShouldContain(Hash("charge[4]"));
    }

    [Fact]
    public async Task Items_after_the_failed_one_do_not_start()
    {
        var ran = new List<string>();
        await using var host = For(
            async context => (object?)await context.Step.MapAsync(
                "charge",
                Letters,
                (item, _) =>
                {
                    ran.Add(item);
                    return item == "b" ? throw new NonRetriableException("declined") : Task.FromResult(item);
                },
                new FanOutOptions { MaxParallelism = 2 }));

        var run = await host.RunAsync("fn", Event(), Cancel);

        run.Status.ShouldBe(TestRunStatus.Failed);
        var failure = run.Error.ShouldBeOfType<StepFailedException>();
        failure.StepId.ShouldBe("charge[1]");
        failure.Error.Message.ShouldBe("declined");
        ran.ShouldBe(["a", "b"]);
        run.Steps.Select(step => step.Id).ShouldBe(["charge[0]", "charge[1]"]);
    }

    [Fact]
    public async Task A_step_error_is_retried_before_the_item_fails_for_good()
    {
        var attempts = 0;
        await using var host = For(
            async context => (object?)await context.Step.MapAsync(
                "charge",
                Letters[..2],
                (item, _) => item == "b" && Interlocked.Increment(ref attempts) < 3 ? throw new HttpRequestException("reset") : Task.FromResult(item)),
            retries: 3);

        var run = await host.RunAsync("fn", Event(), Cancel);

        run.GetOutput<string[]>().ShouldBe(Letters[..2]);
        run.GetStep("charge[1]").Attempts.ShouldBe(3);
    }

    [Fact]
    public async Task Outcomes_say_which_items_failed_and_the_others_still_run()
    {
        var ran = new List<string>();
        await using var host = For(
            async context => (object?)await context.Step.MapOutcomesAsync(
                "charge",
                Letters,
                (item, _) =>
                {
                    ran.Add(item);
                    return item is "b" or "d" ? throw new NonRetriableException($"declined {item}") : Task.FromResult(item);
                },
                new FanOutOptions { MaxParallelism = 2 }));

        var run = await host.RunAsync("fn", Event(), Cancel);

        run.Status.ShouldBe(TestRunStatus.Completed);
        var outcomes = run.GetOutput<FanOutOutcome<string>[]>()!;
        outcomes.Select(outcome => outcome.Index).ShouldBe([0, 1, 2, 3, 4]);
        outcomes.Select(outcome => outcome.Succeeded).ShouldBe([true, false, true, false, true]);
        outcomes.Select(outcome => outcome.Value).ShouldBe(["a", null, "c", null, "e"]);
        outcomes[1].Error!.Message.ShouldBe("declined b");
        outcomes[3].Error!.Message.ShouldBe("declined d");
        ran.Order().ShouldBe(Letters);
    }

    [Fact]
    public async Task A_fake_that_runs_steps_inline_runs_the_whole_list()
    {
        // The extension methods need only the six members every fake of IStepTools implements.
        var step = new InlineSteps();

        var results = await step.MapAsync("charge", Letters, (item, _) => Task.FromResult(item.ToUpperInvariant()), new FanOutOptions { MaxParallelism = 2 });
        var outcomes = await step.MapOutcomesAsync("other", Letters, (item, _) => item == "c" ? throw new InvalidOperationException("no") : Task.FromResult(item));

        results.ShouldBe(["A", "B", "C", "D", "E"]);
        step.Calls.Take(5).ShouldBe(["charge[0]", "charge[1]", "charge[2]", "charge[3]", "charge[4]"]);
        outcomes.Select(outcome => outcome.Succeeded).ShouldBe([true, true, false, true, true]);
        outcomes[2].Error!.Message.ShouldBe("no");
    }

    private static string Hash(string id) => StepIds.Hash(id);
}
