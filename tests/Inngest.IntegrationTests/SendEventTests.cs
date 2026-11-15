using Inngest.IntegrationTests.Functions;
using Inngest.IntegrationTests.Harness;
using Microsoft.Extensions.DependencyInjection;

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

    // Inngest goes on past an event it refuses and keeps the others, so the caller needs to know which went through.
    [Fact(Timeout = 30_000)]
    public async Task A_send_that_Inngest_partly_refuses_says_which_events_it_accepted()
    {
        var client = Fixture.App.Services.GetRequiredService<IInngestClient>();

        var exception = await Should.ThrowAsync<SendEventsException>(client.SendAsync(
            [
                new InngestEvent("itests/unhandled", new { TestId, n = 1 }),
                new InngestEvent("itests/unhandled", new { TestId, n = 2 }) { Timestamp = new DateTimeOffset(1975, 1, 1, 0, 0, 0, TimeSpan.Zero) },
                new InngestEvent("itests/unhandled", new { TestId, n = 3 }),
            ],
            Cancellation));

        exception.Ids.Count.ShouldBe(3);
        exception.Ids[0].ShouldNotBeEmpty();
        exception.Ids[1].ShouldBeEmpty();
        exception.Ids[2].ShouldNotBeEmpty();
        exception.Message.ShouldEndWith("(Inngest accepted 2 of 3 events)");
    }
}
