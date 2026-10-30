using Inngest.IntegrationTests.Evaluation.Functions;
using Inngest.IntegrationTests.Evaluation.Harness;
using Inngest.IntegrationTests.Harness;

namespace Inngest.IntegrationTests.Steps;

// A scoped step over a real call: the scope of the call comes from ASP.NET Core's request, and every execution of a
// step gets a scope next to it, so a step that is retried never sees what its first attempt left behind.
public sealed class ScopedStepTests(EvaluationEnvironment environment) : EvaluationScenario<StepsFixture>(environment)
{
    // The op the trace gives a run step.
    private const string RunOp = "RUN";

    [Fact(Timeout = 120_000)]
    public async Task Every_attempt_of_a_scoped_step_gets_a_scope_of_its_own_and_the_server_sees_run_steps()
    {
        var run = await RunToEndAsync(ScopedStepFunctions.Scoped, new RunEvent(TestId), TimeSpan.FromSeconds(90));

        run.Status.ShouldBe(RunStatus.Completed);
        run.OutputAs<int>().ShouldBe(1);

        var first = Observed.Values<Guid>("first").ShouldHaveSingleItem();
        var flaky = Observed.Values<Guid>("flaky");
        flaky.Count.ShouldBe(2);

        // One scope per execution of a step, none shared with another step, another attempt or the call.
        Guid[] steps = [first, .. flaky];
        steps.Distinct().Count().ShouldBe(3);
        Observed.Values<Guid>("call").ShouldNotContain(id => steps.Contains(id));

        // Each step scope is gone before the call answers, so by the time the run has ended all of them are.
        foreach (var id in steps)
        {
            Observed.Values<Guid>("disposed").ShouldContain(id);
        }

        var trace = await Api.WaitForTraceAsync(
            run.RunId,
            "the steps first and flaky",
            trace => trace.Spans.Any(span => span.Name == "flaky") && trace.Spans.Any(span => span.Name == "first"),
            Cancellation);
        trace.Spans.Where(span => span.Name is "first" or "flaky").Select(span => span.StepOp).ShouldAllBe(op => op == RunOp);
    }
}
