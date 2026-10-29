using Inngest.IntegrationTests.Evaluation.Functions;
using Inngest.IntegrationTests.Evaluation.Harness;
using Inngest.IntegrationTests.Harness;

namespace Inngest.IntegrationTests.Evaluation;

[Trait("Category", "Evaluation")]
[Trait("Area", "Invoke")]
public sealed class InvokeScenarios(EvaluationEnvironment environment) : EvaluationScenario<EvaluationDevFixture>(environment)
{
    [Fact(Timeout = 60_000)]
    public async Task A_chain_of_invokes_returns_through_every_level()
    {
        var run = await RunToEndAsync(InvokeFunctions.Chain, new ValueEvent(TestId, 5), TimeSpan.FromSeconds(20));

        run.Status.ShouldBe(RunStatus.Completed);
        run.OutputAs<int>().ShouldBe(21);

        // B's code before its invoke runs once to start the invoke and once more to return C's result.
        Observed.Named("b").Count.ShouldBe(2);
        Observed.Named("c").Count.ShouldBe(1);
    }

    [Fact(Timeout = 60_000)]
    public async Task A_child_that_fails_all_its_attempts_raises_StepFailedException_in_the_parent()
    {
        var run = await RunToEndAsync(InvokeFunctions.Catch, new RunEvent(TestId), TimeSpan.FromSeconds(20));

        run.Status.ShouldBe(RunStatus.Completed);
        var failure = run.OutputAs<InvokeFailure>();
        failure.Caught.ShouldBeTrue();
        failure.StepId.ShouldBe("call-flaky");
        failure.Message.ShouldNotBeNull().ShouldContain(InvokeFunctions.ChildFailure);
        Observed.Values<int>("child-attempt").ShouldClimbTo(1);
    }

    // An uncaught step failure is final: the parent is not retried, although it has retries left.
    [Fact(Timeout = 60_000)]
    public async Task An_invoke_failure_that_is_not_caught_fails_the_parent_without_retries()
    {
        var run = await RunToEndAsync(InvokeFunctions.Uncaught, new RunEvent(TestId), TimeSpan.FromSeconds(20));

        run.Status.ShouldBe(RunStatus.Failed);
        run.Error.Message.ShouldNotBeNull().ShouldContain(InvokeFunctions.ChildRefusal);
        Observed.Named("child-attempt").Count.ShouldBe(1);
        Observed.Named("parent-saw-failure").Count.ShouldBe(1);
    }

    // The parent gives up when the timeout passes; the child is a run of its own and keeps going.
    [Fact(Timeout = 60_000)]
    public async Task An_invoke_that_times_out_fails_the_step_while_the_child_runs_on()
    {
        var run = await RunToEndAsync(InvokeFunctions.Timeout, new RunEvent(TestId), TimeSpan.FromSeconds(20));

        run.Status.ShouldBe(RunStatus.Completed);
        var failure = run.OutputAs<InvokeFailure>();
        failure.Caught.ShouldBeTrue();
        failure.StepId.ShouldBe("call-sleepy");
        var gaveUp = (await WaitForObservationsAsync("parent-gave-up")).ShouldHaveSingleItem().At;
        var childFinished = (await WaitForObservationsAsync("child-finished", timeout: TimeSpan.FromSeconds(20))).ShouldHaveSingleItem().At;
        childFinished.ShouldBeGreaterThan(gaveUp);
    }

    [Fact(Timeout = 60_000)]
    public async Task Parallel_invokes_each_start_a_run_of_their_own()
    {
        var run = await RunToEndAsync(InvokeFunctions.Parallel, new RunEvent(TestId), TimeSpan.FromSeconds(20));

        run.Status.ShouldBe(RunStatus.Completed);
        run.OutputAs<int[]>().ShouldBe([1, 4, 9, 16, 25]);
        Observed.Values<int>("square").ShouldBe([1, 2, 3, 4, 5], ignoreOrder: true);
    }

    [Fact(Timeout = 60_000)]
    public async Task The_invoked_function_gets_the_data_in_a_function_invoked_event()
    {
        var payload = new NestedPayload(TestId, "Ada", [1, 2, 3], new Dictionary<string, bool> { ["vip"] = true });

        var run = await RunToEndAsync(InvokeFunctions.Data, payload, TimeSpan.FromSeconds(20));

        run.Status.ShouldBe(RunStatus.Completed);
        var child = run.OutputAs<ChildView>();
        child.Name.ShouldBe("inngest/function.invoked");
        child.Data.ShouldContainJson($$"""{ "testId": "{{TestId}}", "customer": "Ada", "items": [1, 2, 3], "flags": { "vip": true } }""");
    }

    [Fact(Timeout = 60_000)]
    public async Task Invoking_a_function_that_does_not_exist_fails_the_step()
    {
        var run = await RunToEndAsync(InvokeFunctions.Missing, new RunEvent(TestId), TimeSpan.FromSeconds(40));

        run.Status.ShouldBe(RunStatus.Completed);
        var failure = run.OutputAs<InvokeFailure>();
        failure.Caught.ShouldBeTrue();
        failure.StepId.ShouldBe("call-nobody");
        failure.Message.ShouldNotBeNullOrEmpty();
    }

    [Fact(Timeout = 60_000)]
    public async Task A_child_that_succeeds_on_a_retry_gives_the_parent_its_result()
    {
        var run = await RunToEndAsync(InvokeFunctions.Recovering, new ValueEvent(TestId, 1), TimeSpan.FromSeconds(20));

        run.Status.ShouldBe(RunStatus.Completed);
        run.OutputAs<int>().ShouldBe(101);
        Observed.Values<int>("child-attempt").ShouldClimbTo(1);
    }
}

[Trait("Category", "Evaluation")]
[Trait("Area", "sendEvent")]
public sealed class SendEventScenarios(EvaluationEnvironment environment) : EvaluationScenario<EvaluationDevFixture>(environment)
{
    [Fact(Timeout = 60_000)]
    public async Task SendEvent_sends_several_events_and_each_starts_a_run_with_its_id()
    {
        var run = await RunToEndAsync(SendEventFunctions.Many, new RunEvent(TestId));

        run.Status.ShouldBe(RunStatus.Completed);
        var ids = run.OutputAs<SentIds>().Ids;
        ids.Count.ShouldBe(3);
        var received = await Task.WhenAll(ids.Select(id => WaitForEndAsync(id)));
        received.Select(child => child.OutputAs<int>()).ShouldBe([1, 2, 3]);
        received.Select(child => child.EventId).ShouldBe(ids);
        Observed.Values<ReceivedEvent>("received").Select(seen => seen.Id).ShouldBe(ids, ignoreOrder: true);
    }

    [Fact(Timeout = 60_000)]
    public async Task A_sent_event_is_not_sent_again_when_the_function_replays_or_retries()
    {
        var run = await RunToEndAsync(SendEventFunctions.Once, new RunEvent(TestId), TimeSpan.FromSeconds(20));

        run.Status.ShouldBe(RunStatus.Completed);
        Observed.Named("flaky").Count.ShouldBe(2);
        await WaitForEndAsync(run.OutputAs<SentIds>().Ids.ShouldHaveSingleItem());
        await Task.Delay(NoRunWindow, Cancellation);
        Observed.Named("received").Count.ShouldBe(1);
    }

    [Fact(Timeout = 60_000)]
    public async Task SendEvent_resumes_a_run_that_waits_for_the_event()
    {
        var waiterId = await SendAsync(SendEventFunctions.WaitStart, new RunEvent(TestId));
        var waiter = await WaitForRunAsync(waiterId);
        await WaitForStepAsync(waiter.RunId, StepOps.WaitForEvent);

        var signaler = await RunToEndAsync(SendEventFunctions.SignalStart, new ValueEvent(TestId, 99));
        var resumed = await WaitForEndAsync(waiterId);

        signaler.Status.ShouldBe(RunStatus.Completed);
        resumed.Status.ShouldBe(RunStatus.Completed);
        resumed.OutputAs<int>().ShouldBe(99);
    }

    // The server schedules events concurrently, so the two sends race for the one run: the run is listed under either
    // event and gets that event's data. What the id guarantees is that only one of them runs.
    [Fact(Timeout = 60_000)]
    public async Task Events_sent_from_two_steps_with_the_same_id_start_one_run()
    {
        var run = await RunToEndAsync(SendEventFunctions.Dedupe, new RunEvent(TestId));

        run.Status.ShouldBe(RunStatus.Completed);
        var sends = run.OutputAs<TwoSends>();
        await WaitForObservationsAsync("received");
        await Task.Delay(NoRunWindow, Cancellation);
        (await RunsOfAsync(sends.First.ShouldHaveSingleItem(), sends.Second.ShouldHaveSingleItem())).ShouldHaveSingleItem();
        var received = Observed.Values<ReceivedEvent>("received").ShouldHaveSingleItem();
        received.Id.ShouldBe($"dedupe-{TestId}");
        received.Value.ShouldBeOneOf(1, 2);
    }

    [Fact(Timeout = 60_000)]
    public async Task A_sent_event_keeps_its_name_id_timestamp_and_version()
    {
        var run = await RunToEndAsync(SendEventFunctions.Described, new RunEvent(TestId));

        run.Status.ShouldBe(RunStatus.Completed);
        await WaitForEndAsync(run.OutputAs<SentIds>().Ids.ShouldHaveSingleItem());
        var seen = Observed.Values<ReceivedEvent>("received").ShouldHaveSingleItem();
        seen.ShouldBe(new ReceivedEvent(5, $"described-{TestId}", SendEventFunctions.DescribedTimestamp, "2026-01-02.1", SendEventFunctions.Received));
    }
}
