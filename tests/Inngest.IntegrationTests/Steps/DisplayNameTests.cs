using Inngest.IntegrationTests.Evaluation.Functions;
using Inngest.IntegrationTests.Evaluation.Harness;
using Inngest.IntegrationTests.Harness;

namespace Inngest.IntegrationTests.Steps;

public sealed class DisplayNameTests(EvaluationEnvironment environment) : EvaluationScenario<StepsFixture>(environment)
{
    [Fact(Timeout = 60_000)]
    public async Task The_run_trace_shows_steps_under_their_display_names()
    {
        var run = await RunToEndAsync(NamedStepFunctions.Named, new RunEvent(TestId));

        run.Status.ShouldBe(RunStatus.Completed);
        run.OutputAs<string>().ShouldBe("R-1");
        var trace = await Api.WaitForTraceAsync(
            run.RunId,
            "the trace to list the steps",
            trace => trace.Spans.Any(span => span.Name == "ship"),
            Cancellation);
        var names = trace.Spans.Select(span => span.Name).ToList();
        names.ShouldContain("Charge the card");
        names.ShouldContain("Cool off");
        names.ShouldNotContain("charge");
        names.ShouldNotContain("cool-off");
    }
}
