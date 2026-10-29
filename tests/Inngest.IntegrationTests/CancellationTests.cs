using Inngest.IntegrationTests.Functions;
using Inngest.IntegrationTests.Harness;

namespace Inngest.IntegrationTests;

public sealed class CancellationTests(DevServerFixture fixture) : IntegrationTest<DevServerFixture>(fixture)
{
    [Fact(Timeout = 30_000)]
    public async Task CancelOn_event_with_a_matching_field_cancels_a_sleeping_run()
    {
        var eventId = await Api.SendEventAsync("itests/cancel.start", new TestEvent(TestId), Cancellation);
        var run = await Api.WaitForRunAsync(eventId, Cancellation);
        await Api.WaitForStepAsync(run.RunId, StepOps.Sleep, SpanStatuses.Waiting, Cancellation);

        await Api.SendEventAsync("itests/cancel.stop", new TestEvent(TestId), Cancellation);
        var ended = await Api.WaitForRunEndAsync(eventId, Cancellation);

        ended.Status.ShouldBe(RunStatus.Cancelled);
        Observed.Names.ShouldBe(["started"]);
    }
}
