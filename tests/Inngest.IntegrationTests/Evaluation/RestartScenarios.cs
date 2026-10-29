using Inngest.IntegrationTests.Evaluation.Functions;
using Inngest.IntegrationTests.Evaluation.Harness;
using Inngest.IntegrationTests.Harness;

namespace Inngest.IntegrationTests.Evaluation;

// The app is stopped and a new instance started on the same port, as a deployment would, while Inngest keeps the run.
// They run in the isolated collection: while the app is down its server keeps dialing a closed port, which stalls
// Docker's host networking for the other servers too.
[Collection(IsolatedCollection.Name)]
[Trait("Category", "Evaluation")]
[Trait("Area", "Durability")]
public sealed class RestartScenarios(EvaluationEnvironment environment) : EvaluationScenario<RestartFixture>(environment)
{
    public override async ValueTask InitializeAsync()
    {
        await base.InitializeAsync();
        await Fixture.EnsureRunningAsync(Cancellation);
    }

    [Fact(Timeout = 60_000)]
    public async Task A_run_sleeping_through_an_app_restart_resumes_on_the_new_instance()
    {
        var eventId = await SendAsync(RestartFunctions.Restart, new RunEvent(TestId));
        var run = await WaitForRunAsync(eventId);
        await WaitForObservationsAsync("before");
        var oldInstance = Fixture.InstanceId;
        await WaitForStepAsync(run.RunId, StepOps.Sleep);

        await Fixture.StopAppAsync();
        await Fixture.StartAppAsync(Cancellation);
        var ended = await WaitForEndAsync(eventId, TimeSpan.FromSeconds(30));

        ended.Status.ShouldBe(RunStatus.Completed);
        ended.OutputAs<string>().ShouldBe(Fixture.InstanceId);
        Observed.Values<string>("before").ShouldBe([oldInstance]);
        Observed.Values<string>("after").ShouldBe([Fixture.InstanceId]);
        Fixture.InstanceId.ShouldNotBe(oldInstance);
    }

    // The sleep ends while the app is down, so the server's calls fail until the new instance is up.
    [Fact(Timeout = 60_000)]
    public async Task A_step_due_while_the_app_is_down_runs_once_the_app_is_back()
    {
        var eventId = await SendAsync(RestartFunctions.Outage, new RunEvent(TestId));
        var run = await WaitForRunAsync(eventId);
        await WaitForObservationsAsync("before");
        var oldInstance = Fixture.InstanceId;
        await WaitForStepAsync(run.RunId, StepOps.Sleep);

        await Fixture.StopAppAsync();
        await Task.Delay(RestartFunctions.OutageNap + TimeSpan.FromSeconds(2), Cancellation);
        var restartedAt = DateTimeOffset.UtcNow;
        await Fixture.StartAppAsync(Cancellation);
        var ended = await WaitForEndAsync(eventId, TimeSpan.FromSeconds(30));

        ended.Status.ShouldBe(RunStatus.Completed);
        Observed.Values<string>("before").ShouldBe([oldInstance]);
        var after = Observed.Named("after").ShouldHaveSingleItem();
        after.Value.ShouldBe(Fixture.InstanceId);
        after.At.ShouldBeGreaterThan(restartedAt);
    }

    // Stopping the app aborts the step it is running once the shutdown timeout passes; Inngest retries the step, and
    // the new instance runs it again. Steps run at least once, so the step body sees both executions.
    [Fact(Timeout = 60_000)]
    public async Task A_step_cut_off_by_an_app_shutdown_runs_again_on_the_new_instance()
    {
        var eventId = await SendAsync(RestartFunctions.Interrupted, new RunEvent(TestId));
        await WaitForObservationsAsync("started");
        var oldInstance = Fixture.InstanceId;

        await Fixture.StopAppAsync();
        await Fixture.StartAppAsync(Cancellation);
        var ended = await WaitForEndAsync(eventId, TimeSpan.FromSeconds(30));

        ended.Status.ShouldBe(RunStatus.Completed);
        ended.OutputAs<string>().ShouldBe($"finished on {Fixture.InstanceId}");
        Observed.Values<string>("started").ShouldBe([oldInstance, Fixture.InstanceId]);
    }
}
