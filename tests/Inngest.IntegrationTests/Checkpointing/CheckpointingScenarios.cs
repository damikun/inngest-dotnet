using Inngest.IntegrationTests.Evaluation.Functions;
using Inngest.IntegrationTests.Evaluation.Harness;
using Inngest.IntegrationTests.Harness;

namespace Inngest.IntegrationTests.Checkpointing;

// Checkpointing against the dev server: steps saved through POST /v1/checkpoint/{runId}/async while the call goes on,
// and the classic answers wherever the run needs Inngest.
[Trait("Category", "Evaluation")]
[Trait("Area", "Checkpointing")]
public sealed class CheckpointingScenarios(EvaluationEnvironment environment) : EvaluationScenario<CheckpointingFixture>(environment)
{
    [Fact(Timeout = 60_000)]
    public async Task A_run_of_consecutive_steps_takes_one_call_where_it_took_one_per_step()
    {
        var classicId = TestId + "-classic";
        var checkpointed = await RunToEndAsync(CheckpointingFunctions.Chain, new ValueEvent(TestId, 5));
        var classic = await RunToEndAsync(CheckpointingFunctions.ClassicChain, new ValueEvent(classicId, 5));

        checkpointed.Status.ShouldBe(RunStatus.Completed);
        classic.Status.ShouldBe(RunStatus.Completed);
        checkpointed.OutputAs<StepsResult>().Steps.ShouldBe(["step-1", "step-2", "step-3", "step-4", "step-5"]);
        classic.OutputAs<StepsResult>().Steps.ShouldBe(checkpointed.OutputAs<StepsResult>().Steps);
        Observed.Names.ShouldBe(["call", "step-1", "step-2", "step-3", "step-4", "step-5"]);
        Fixture.Probe.For(classicId).Named("call").Count.ShouldBe(6);
        Fixture.CallsOf("ckpt-chain").ShouldHaveSingleItem().StatusCode.ShouldBe(200);
        Fixture.CallsOf("ckpt-classic-chain").Count.ShouldBe(6);

        // The steps are saved: the server shows each one, though no answer carried them.
        var trace = await Api.WaitForTraceAsync(
            checkpointed.RunId,
            "five completed RUN steps",
            trace => trace.Spans.Count(span => span.StepOp == "RUN" && span.Status == "COMPLETED") == 5,
            Cancellation);
        trace.Status.ShouldBe("COMPLETED");
    }

    [Fact(Timeout = 60_000)]
    public async Task A_sleep_hands_the_run_back_to_Inngest_and_the_next_call_goes_on_from_there()
    {
        var run = await RunToEndAsync(CheckpointingFunctions.Sleep, new RunEvent(TestId), TimeSpan.FromSeconds(20));

        run.Status.ShouldBe(RunStatus.Completed);
        run.OutputAs<StepsResult>().Steps.ShouldBe(["a", "b"]);
        Observed.Names.ShouldBe(["call", "a", "call", "b"]);
        Fixture.CallsOf("ckpt-sleep").Select(call => call.StatusCode).ShouldBe([206, 200]);
        var gap = Observed.Named("b")[0].At - Observed.Named("a")[0].At;
        gap.ShouldBeGreaterThanOrEqualTo(CheckpointingFunctions.Nap - TimeSpan.FromMilliseconds(100));
    }

    [Fact(Timeout = 60_000)]
    public async Task A_failing_step_is_retried_in_a_call_of_its_own_and_the_run_then_goes_on()
    {
        var run = await RunToEndAsync(CheckpointingFunctions.Retry, new RunEvent(TestId), TimeSpan.FromSeconds(20));

        run.Status.ShouldBe(RunStatus.Completed);
        run.OutputAs<AttemptsResult>().ShouldBe(new AttemptsResult("a", "b", 1, "c", 0));
        Observed.Names.Where(name => name != "call").ShouldBe(["a", "b", "b", "c"]);

        // The step error, the retry that answers right after the step, then the rest of the run.
        Fixture.CallsOf("ckpt-retry").Select(call => call.StatusCode).ShouldBe([206, 206, 200]);
    }

    [Fact(Timeout = 60_000)]
    public async Task Parallel_steps_are_planned_by_Inngest_and_each_runs_once()
    {
        var run = await RunToEndAsync(CheckpointingFunctions.Parallel, new RunEvent(TestId), TimeSpan.FromSeconds(20));

        run.Status.ShouldBe(RunStatus.Completed);
        run.OutputAs<StepsResult>().Steps.ShouldBe(["a", "b", "c", "d"]);
        foreach (var step in new[] { "a", "b", "c", "d" })
        {
            Observed.Named(step).Count.ShouldBe(1, $"step {step} ran {Observed.Named(step).Count} times");
        }

        // The first call saves a and plans b and c, which run in calls of their own. Once both are done, checkpointing
        // goes on: one more call runs d and completes the run.
        var calls = Fixture.CallsOf("ckpt-parallel");
        calls.Count.ShouldBe(4);
        calls.Select(call => (call.Target.EndsWith("stepId=step", StringComparison.Ordinal), call.StatusCode))
            .ShouldBe([(true, 206), (false, 206), (false, 206), (true, 200)]);
    }
}
