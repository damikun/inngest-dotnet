using Inngest.IntegrationTests.Functions;
using Inngest.IntegrationTests.Harness;

namespace Inngest.IntegrationTests;

public sealed class LogScopeTests(DevServerFixture fixture) : IntegrationTest<DevServerFixture>(fixture)
{
    [Fact(Timeout = 30_000)]
    public async Task A_service_logs_with_the_runs_context_in_a_step_and_after_it()
    {
        var run = await RunToEndAsync("itests/log.scope", new TestEvent(TestId));

        run.Status.ShouldBe(RunStatus.Completed);
        Fixture.App.Logs.ShouldHaveLoggedWithRunScopes(TestId, "log-scope", run.RunId, "itests/log.scope", Api.BaseUrl);
    }
}
