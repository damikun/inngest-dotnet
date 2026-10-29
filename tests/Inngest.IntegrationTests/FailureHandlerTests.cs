using Inngest.IntegrationTests.Functions;
using Inngest.IntegrationTests.Harness;

namespace Inngest.IntegrationTests;

public sealed class FailureHandlerTests(DevServerFixture fixture) : IntegrationTest<DevServerFixture>(fixture)
{
    [Fact(Timeout = 30_000)]
    public async Task Failure_handler_runs_once_with_the_original_event_and_the_error()
    {
        var run = await RunToEndAsync("itests/always.fails", new TestEvent(TestId));

        run.Status.ShouldBe(RunStatus.Failed);
        run.Error.Message.ShouldBe(AlwaysFailsFunction.MessageFor(TestId));
        Observed.Values<int>("attempt").ShouldBe([0, 1]);

        var handled = await Observed.WaitForAsync("on-failure", TimeSpan.FromSeconds(20), Cancellation);
        var seen = handled.Value.ShouldBeOfType<FailureSeen>();
        seen.TriggerName.ShouldBe("inngest/function.failed");
        seen.FunctionId.ShouldBe("itests-always-fails");
        seen.RunId.ShouldBe(run.RunId);
        seen.ErrorMessage.ShouldBe(AlwaysFailsFunction.MessageFor(TestId));
        seen.OriginalEventName.ShouldBe("itests/always.fails");
        Observed.Named("on-failure").Count.ShouldBe(1);
    }
}
