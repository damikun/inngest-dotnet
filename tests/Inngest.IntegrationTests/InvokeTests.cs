using Inngest.IntegrationTests.Functions;
using Inngest.IntegrationTests.Harness;

namespace Inngest.IntegrationTests;

public sealed class InvokeTests(DevServerFixture fixture) : IntegrationTest<DevServerFixture>(fixture)
{
    [Fact(Timeout = 30_000)]
    public async Task Invoke_returns_the_output_of_the_invoked_function()
    {
        var run = await RunToEndAsync("itests/invoke.parent", new NumberEvent(TestId, 21));

        run.Status.ShouldBe(RunStatus.Completed);
        run.OutputAs<ParentResult>().ShouldBe(new ParentResult(new NumberResult(42)));
        Observed.Values<int>("child").ShouldBe([21]);
    }

    [Fact(Timeout = 30_000)]
    public async Task Failure_of_the_invoked_function_surfaces_as_StepFailedException()
    {
        var run = await RunToEndAsync("itests/invoke.catches", new TestEvent(TestId));

        run.Status.ShouldBe(RunStatus.Completed);
        var caught = run.OutputAs<CaughtFailure>();
        caught.Caught.ShouldBeTrue();
        caught.StepId.ShouldBe(InvokeCatchesFailureFunction.StepId);
        caught.Message.ShouldNotBeNull().ShouldContain(FailingChildFunction.Message);
        Observed.Values<int>("child-attempt").ShouldBe([0]);
    }
}
