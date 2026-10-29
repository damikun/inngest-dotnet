using Inngest.IntegrationTests.Functions;
using Inngest.IntegrationTests.Harness;

namespace Inngest.IntegrationTests;

public sealed class StepTests(DevServerFixture fixture) : IntegrationTest<DevServerFixture>(fixture)
{
    [Fact(Timeout = 30_000)]
    public async Task Step_results_flow_into_later_steps_and_each_step_body_runs_once()
    {
        var run = await RunToEndAsync("itests/step.sequence", new NumberEvent(TestId, 4));

        run.Status.ShouldBe(RunStatus.Completed);
        run.OutputAs<SequenceResult>().ShouldBe(new SequenceResult(5, 50, "5:50"));
        Observed.Names.ShouldBe(["first", "second", "third"]);
    }

    [Fact(Timeout = 30_000)]
    public async Task Parallel_steps_each_run_once_and_their_results_combine()
    {
        var run = await RunToEndAsync("itests/parallel", new NumberEvent(TestId, 7));

        run.Status.ShouldBe(RunStatus.Completed);
        var output = run.OutputAs<ParallelResult>();
        output.Parts.ShouldBe([7, 14, 21]);
        output.Total.ShouldBe(42);
        Observed.Names.Take(3).ShouldBe(["part-1", "part-2", "part-3"], ignoreOrder: true);
        Observed.Names.Skip(3).ShouldBe(["total"]);
    }
}
