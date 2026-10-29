using Inngest.IntegrationTests.Functions;
using Inngest.IntegrationTests.Harness;

namespace Inngest.IntegrationTests;

public sealed class SleepTests(DevServerFixture fixture) : IntegrationTest<DevServerFixture>(fixture)
{
    // The server runs in Docker's virtual machine, whose clock can be slightly off from the host's.
    // The server schedules wake-ups on its own clock, and the clock of Docker's VM drifts from the host's by
    // hundreds of milliseconds. A broken sleep would wake seconds early, so a second of slack still proves it.
    private static readonly TimeSpan ClockTolerance = TimeSpan.FromSeconds(1);

    [Fact(Timeout = 30_000)]
    public async Task Sleep_pauses_the_run_between_two_steps()
    {
        var run = await RunToEndAsync("itests/sleep", new TestEvent(TestId));

        run.Status.ShouldBe(RunStatus.Completed);
        var before = Observed.Named("before-nap").ShouldHaveSingleItem().At;
        var after = Observed.Named("after-nap").ShouldHaveSingleItem().At;
        (after - before).ShouldBeInRange(SleepFunction.Duration, SleepFunction.Duration + TimeSpan.FromSeconds(10));
    }

    [Fact(Timeout = 30_000)]
    public async Task SleepUntil_pauses_the_run_until_the_given_time()
    {
        // A whole second, so the test does not depend on how precisely the SDK writes the time.
        var now = DateTimeOffset.UtcNow;
        var until = new DateTimeOffset(now.Ticks - (now.Ticks % TimeSpan.TicksPerSecond), TimeSpan.Zero).AddSeconds(3);

        var run = await RunToEndAsync("itests/sleep.until", new SleepUntilEvent(TestId, until));

        run.Status.ShouldBe(RunStatus.Completed);
        Observed.Named("woke").ShouldHaveSingleItem().At.ShouldBeGreaterThan(until - ClockTolerance);
    }
}
