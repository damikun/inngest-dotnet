using Inngest.IntegrationTests.Functions;
using Inngest.IntegrationTests.Harness;

namespace Inngest.IntegrationTests;

// The server retries failed calls after one second (--retry-interval 1), unless the SDK asks for a later time.
public sealed class RetryTests(DevServerFixture fixture) : IntegrationTest<DevServerFixture>(fixture)
{
    [Fact(Timeout = 30_000)]
    public async Task Function_that_fails_its_first_attempt_succeeds_on_the_retry()
    {
        var run = await RunToEndAsync("itests/retry.function", new TestEvent(TestId));

        run.Status.ShouldBe(RunStatus.Completed);
        run.OutputAs<AttemptResult>().ShouldBe(new AttemptResult(1));
        Observed.Values<int>("attempt").ShouldBe([0, 1]);
    }

    [Fact(Timeout = 30_000)]
    public async Task Step_that_fails_twice_is_retried_until_it_succeeds()
    {
        var run = await RunToEndAsync("itests/retry.step", new TestEvent(TestId));

        run.Status.ShouldBe(RunStatus.Completed);
        run.OutputAs<TextResult>().ShouldBe(new TextResult("recovered"));
        Observed.Values<int>("flaky").ShouldBe([0, 1, 2]);
    }

    [Fact(Timeout = 30_000)]
    public async Task NonRetriableException_fails_the_run_after_a_single_attempt()
    {
        var run = await RunToEndAsync("itests/non.retriable", new TestEvent(TestId));

        run.Status.ShouldBe(RunStatus.Failed);
        run.Error.Message.ShouldBe(NonRetriableFunction.Message);
        Observed.Values<int>("attempt").ShouldBe([0]);
    }

    [Fact(Timeout = 30_000)]
    public async Task RetryAfterException_holds_the_retry_back_until_the_delay_has_passed()
    {
        var run = await RunToEndAsync("itests/retry.after", new TestEvent(TestId));

        run.Status.ShouldBe(RunStatus.Completed);
        Observed.Values<int>("attempt").ShouldBe([0, 1]);

        // Without the delay the retry would follow after about a second. In the protocol captures a Retry-After of
        // 3 s led to a retry 2.7 s later, so allow up to a second less than asked.
        var attempts = Observed.Named("attempt");
        (attempts[1].At - attempts[0].At).ShouldBeGreaterThan(RetryAfterFunction.Delay - TimeSpan.FromSeconds(1));
    }
}
