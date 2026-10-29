using Inngest.IntegrationTests.Evaluation.Functions;
using Inngest.IntegrationTests.Evaluation.Harness;
using Inngest.IntegrationTests.Harness;
using Microsoft.Extensions.DependencyInjection;

namespace Inngest.IntegrationTests.Rest;

// The app's IInngestRunsClient against the dev server. The harness waits for runs with cache-busting queries; the client
// sends plain ones, which the server caches (a run for 3 s, the runs of an event for 15 s), so each test reads a URL
// the first time only once the state it checks is final, or polls for longer than the cache lasts.
public sealed class RunsClientTests(EvaluationEnvironment environment) : EvaluationScenario<RunsFixture>(environment)
{
    // A valid run id that no run has.
    private const string UnknownRunId = "01K8ZZZZZZZZZZZZZZZZZZZZZZ";

    private IInngestRunsClient Runs => Fixture.App.Services.GetRequiredService<IInngestRunsClient>();

    [Fact(Timeout = 60_000)]
    public async Task Gets_the_status_and_output_of_a_finished_run()
    {
        var eventId = await SendAsync(RunsFunctions.Greet, new RunEvent(TestId));
        var ended = await WaitForEndAsync(eventId);

        var run = await Runs.GetRunAsync(ended.RunId, Cancellation);

        run.RunId.ShouldBe(ended.RunId);
        run.Status.ShouldBe(FunctionRunStatus.Completed);
        run.EventId.ShouldBe(eventId);
        run.FunctionId.ShouldBe(ended.FunctionId);
        run.EndedAt.ShouldNotBeNull();
        run.EndedAt.Value.ShouldBeGreaterThanOrEqualTo(run.StartedAt);
        run.GetOutput<Greeting>().ShouldBe(new Greeting("hi dalibor", TestId));
    }

    [Fact(Timeout = 60_000)]
    public async Task Lists_the_runs_an_event_started()
    {
        var eventId = await SendAsync(RunsFunctions.FanOut, new RunEvent(TestId));
        var ended = await WaitForRunsToEndAsync(eventId, 2);

        var runs = await Runs.GetEventRunsAsync(eventId, Cancellation);

        runs.Select(run => run.RunId).ShouldBe(ended.Select(run => run.RunId), ignoreOrder: true);
        runs.ShouldAllBe(run => run.Status == FunctionRunStatus.Completed && run.EventId == eventId && run.EndedAt != null);
        runs.Select(run => run.GetOutput<Branch>()).ShouldBe([new Branch("a", TestId), new Branch("b", TestId)], ignoreOrder: true);
    }

    [Fact(Timeout = 60_000)]
    public async Task Cancels_a_sleeping_run()
    {
        var eventId = await SendAsync(RunsFunctions.Nap, new RunEvent(TestId));
        var started = await WaitForRunAsync(eventId);
        await WaitForStepAsync(started.RunId, StepOps.Sleep);

        await Runs.CancelRunAsync(started.RunId, Cancellation);

        var cancelled = await Poll.UntilAsync(
            $"run {started.RunId} to read as cancelled",
            token => Runs.GetRunAsync(started.RunId, token),
            run => run.Status == FunctionRunStatus.Cancelled,
            TimeSpan.FromSeconds(15),
            Fixture.DescribeAsync,
            Cancellation);
        cancelled.EndedAt.ShouldNotBeNull();
        (await Api.GetRunAsync(started.RunId, Cancellation)).Status.ShouldBe(RunStatus.Cancelled);
    }

    [Fact(Timeout = 60_000)]
    public async Task Fails_with_the_reason_inngest_gives_for_a_run_it_does_not_have()
    {
        var get = await Should.ThrowAsync<InngestException>(Runs.GetRunAsync(UnknownRunId, Cancellation));
        var cancel = await Should.ThrowAsync<InngestException>(Runs.CancelRunAsync(UnknownRunId, Cancellation));

        // The dev server answers a read of an unknown run with a 500, and a cancel of one with a 404.
        get.Message.ShouldBe($"Failed to get run {UnknownRunId}: Unable to load function run: {UnknownRunId}");
        get.StatusCode.ShouldBe(500);
        cancel.Message.ShouldBe($"Failed to cancel run {UnknownRunId}: Unable to load function run: {UnknownRunId}");
        cancel.StatusCode.ShouldBe(404);
    }
}
