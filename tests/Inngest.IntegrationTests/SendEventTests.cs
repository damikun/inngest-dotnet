using Inngest.IntegrationTests.Functions;
using Inngest.IntegrationTests.Harness;

namespace Inngest.IntegrationTests;

public sealed class SendEventTests(DevServerFixture fixture) : IntegrationTest<DevServerFixture>(fixture)
{
    [Fact(Timeout = 30_000)]
    public async Task SendEvent_step_starts_the_function_that_the_event_triggers()
    {
        var run = await RunToEndAsync("itests/send.parent", new NumberEvent(TestId, 9));

        run.Status.ShouldBe(RunStatus.Completed);
        var sentId = run.OutputAs<SentResult>().Ids.ShouldHaveSingleItem();
        var received = await Api.WaitForRunEndAsync(sentId, Cancellation);
        received.Status.ShouldBe(RunStatus.Completed);
        received.OutputAs<NumberResult>().ShouldBe(new NumberResult(10));
        Observed.Values<int>("received").ShouldBe([10]);
    }
}
