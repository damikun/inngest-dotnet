using Inngest.IntegrationTests.Functions;
using Inngest.IntegrationTests.Harness;

namespace Inngest.IntegrationTests;

public sealed class WaitForEventTests(DevServerFixture fixture) : IntegrationTest<DevServerFixture>(fixture)
{
    [Fact(Timeout = 30_000)]
    public async Task WaitForEvent_returns_the_event_that_matches()
    {
        var eventId = await Api.SendEventAsync("itests/wait.start", new WaitStartEvent(TestId, TimeoutSeconds: 60), Cancellation);
        var run = await Api.WaitForRunAsync(eventId, Cancellation);

        // An event sent before the wait is stored would be missed.
        await Api.WaitForStepAsync(run.RunId, StepOps.WaitForEvent, SpanStatuses.Waiting, Cancellation);

        // Sent first but for another test id, so it must not resume this run.
        await Api.SendEventAsync(WaitForEventFunction.ResumeEventName, new ResumeEvent(NewTestId(), "not for this run"), Cancellation);
        await Api.SendEventAsync(WaitForEventFunction.ResumeEventName, new ResumeEvent(TestId, "resume!"), Cancellation);
        var ended = await Api.WaitForRunEndAsync(eventId, Cancellation);

        ended.Status.ShouldBe(RunStatus.Completed);
        ended.OutputAs<WaitResult>().ShouldBe(new WaitResult(true, WaitForEventFunction.ResumeEventName, "resume!"));
    }

    [Fact(Timeout = 30_000)]
    public async Task WaitForEvent_returns_null_when_no_event_arrives_in_time()
    {
        var run = await RunToEndAsync("itests/wait.start", new WaitStartEvent(TestId, TimeoutSeconds: 1));

        run.Status.ShouldBe(RunStatus.Completed);
        run.OutputAs<WaitResult>().ShouldBe(new WaitResult(false, null, null));
    }
}
