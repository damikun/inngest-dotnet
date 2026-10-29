using Inngest.IntegrationTests.Evaluation.Functions;
using Inngest.IntegrationTests.Evaluation.Harness;
using Inngest.IntegrationTests.Harness;

namespace Inngest.IntegrationTests.Steps;

// Approved or rejected: two waits for an event. Inngest calls the function again only once every step started
// together has finished, unless they race, so without race mode the run goes on only when the other wait timed out.
public sealed class WaitRaceTests(EvaluationEnvironment environment) : EvaluationScenario<StepsFixture>(environment)
{
    // Well under the timeout of the wait that loses, and well over the few calls through the dev server it takes.
    private static readonly TimeSpan Prompt = TimeSpan.FromSeconds(4);

    // The server times the waits on the clock of Docker's VM, which drifts from the host's by a few hundred milliseconds.
    private static readonly TimeSpan Tolerance = TimeSpan.FromSeconds(1);

    [Fact(Timeout = 60_000)]
    public async Task In_race_mode_the_run_goes_on_as_soon_as_the_first_event_arrives()
    {
        var eventId = await StartAsync(RaceFunctions.Waits);

        var sentAt = DateTimeOffset.UtcNow;
        await SendAsync(RaceFunctions.Approved, new Verdict(TestId, "manager"));
        var decided = (await WaitForObservationsAsync("decided")).ShouldHaveSingleItem();

        decided.Value.ShouldBe(RaceFunctions.Approved);
        (decided.At - sentAt).ShouldBeLessThan(Prompt);

        // The wait that lost still waits; once it ends too, the run completes with the winner it went on with.
        await SendAsync(RaceFunctions.Rejected, new Verdict(TestId, "auditor"));
        var run = await WaitForEndAsync(eventId);
        run.Status.ShouldBe(RunStatus.Completed);
        run.OutputAs<string>().ShouldBe(RaceFunctions.Approved);
        Observed.Named("decided").Count.ShouldBe(1);
    }

    [Fact(Timeout = 60_000)]
    public async Task Without_race_mode_the_run_goes_on_only_once_the_other_wait_timed_out()
    {
        var eventId = await StartAsync(RaceFunctions.WaitsWithoutRaceMode);

        var sentAt = DateTimeOffset.UtcNow;
        await SendAsync(RaceFunctions.Approved, new Verdict(TestId, "manager"));
        var decided = (await WaitForObservationsAsync("decided", timeout: RaceFunctions.LoserTimeout + DevServerClient.DefaultTimeout))
            .ShouldHaveSingleItem();

        decided.Value.ShouldBe(RaceFunctions.Approved);
        (decided.At - sentAt).ShouldBeGreaterThan(Prompt);
        (decided.At - Observed.Named("waiting").ShouldHaveSingleItem().At).ShouldBeGreaterThan(RaceFunctions.LoserTimeout - Tolerance);
        var run = await WaitForEndAsync(eventId);
        run.Status.ShouldBe(RunStatus.Completed);
        run.OutputAs<string>().ShouldBe(RaceFunctions.Approved);
    }

    [Fact(Timeout = 60_000)]
    public async Task Steps_after_the_race_go_ahead_before_the_other_wait_ends_when_they_race_too()
    {
        var eventId = await StartAsync(RaceFunctions.WaitsThenSteps);

        var sentAt = DateTimeOffset.UtcNow;
        await SendAsync(RaceFunctions.Approved, new Verdict(TestId, "manager"));
        var archived = (await WaitForObservationsAsync("archived")).ShouldHaveSingleItem();

        (archived.At - sentAt).ShouldBeLessThan(Prompt);
        Observed.Names.ShouldBe(["waiting", "notified", "archived"]);
        await SendAsync(RaceFunctions.Rejected, new Verdict(TestId, "auditor"));
        var run = await WaitForEndAsync(eventId);
        run.OutputAs<string>().ShouldBe(RaceFunctions.Approved);
    }

    // Starts the run and waits until the server has stored both waits, so that an event sent now resumes one.
    private async Task<string> StartAsync(string name)
    {
        var eventId = await SendAsync(name, new RunEvent(TestId));
        var run = await WaitForRunAsync(eventId);
        await WaitForStepAsync(run.RunId, StepOps.WaitForEvent, count: 2);
        return eventId;
    }
}

public sealed class StepRaceTests(EvaluationEnvironment environment) : EvaluationScenario<StepsFixture>(environment)
{
    // The slow step is held at a gate until the step after the race has run, which only race mode allows.
    [Fact(Timeout = 60_000)]
    public async Task A_race_of_run_steps_goes_on_while_the_slower_step_still_runs()
    {
        var eventId = await SendAsync(RaceFunctions.Runs, new RunEvent(TestId));

        await WaitForObservationsAsync("after");
        Fixture.Gates.Open(TestId, RaceFunctions.SlowGate);
        var run = await WaitForEndAsync(eventId, TimeSpan.FromSeconds(30));

        run.Status.ShouldBe(RunStatus.Completed);
        run.OutputAs<string>().ShouldBe("fast");
        Observed.Named("fast").Count.ShouldBe(1);
        Observed.Named("slow").Count.ShouldBe(1);
        Observed.Values<string>("after").ShouldBe(["fast"]);
    }

    // C is held until B has run and the call after B, where C is the only step left without a result, is over.
    // C must not run a second time in that call.
    [Fact(Timeout = 60_000)]
    public async Task A_branch_of_steps_in_sequence_goes_ahead_while_another_branch_runs_and_every_step_runs_once()
    {
        var eventId = await SendAsync(RaceFunctions.Branches, new RunEvent(TestId));

        await WaitForObservationsAsync("B");
        await Task.Delay(TimeSpan.FromSeconds(1), Cancellation);
        Fixture.Gates.Open(TestId, RaceFunctions.SlowGate);
        var run = await WaitForEndAsync(eventId, TimeSpan.FromSeconds(30));

        run.Status.ShouldBe(RunStatus.Completed);
        run.OutputAs<string>().ShouldBe("1+1,C");
        Observed.Names.Order().ShouldBe(["A", "B", "C", "Final"]);
        Observed.Names[^1].ShouldBe("Final");
    }

    [Fact(Timeout = 60_000)]
    public async Task A_sleep_in_a_race_cuts_a_long_wait_short()
    {
        var eventId = await SendAsync(RaceFunctions.Sleep, new RunEvent(TestId));

        var outcome = (await WaitForObservationsAsync("outcome")).ShouldHaveSingleItem();
        outcome.Value.ShouldBe("escalated");
        (outcome.At - Observed.Named("waiting").ShouldHaveSingleItem().At).ShouldBeLessThan(TimeSpan.FromSeconds(5));

        // The wait still waits; an approval that comes late ends the run without changing what it decided.
        await SendAsync(RaceFunctions.Approved, new Verdict(TestId, "late manager"));
        var run = await WaitForEndAsync(eventId);
        run.Status.ShouldBe(RunStatus.Completed);
        run.OutputAs<string>().ShouldBe("escalated");
    }
}
