using Inngest.IntegrationTests.Evaluation.Functions;
using Inngest.IntegrationTests.Evaluation.Harness;
using Inngest.IntegrationTests.Harness;
using Microsoft.Extensions.DependencyInjection;

namespace Inngest.IntegrationTests.Evaluation;

// The server schedules wake-ups on the clock of Docker's VM, which drifts from the host's by up to a few hundred
// milliseconds. Durations here are measured on the app's clock between two steps, so a broken sleep, which wakes early
// by whole seconds, still shows. Upper bounds leave room for a call the server has to retry.
[Trait("Category", "Evaluation")]
[Trait("Area", "Sleep and waitForEvent")]
public sealed class SleepScenarios(EvaluationEnvironment environment) : EvaluationScenario<EvaluationDevFixture>(environment)
{
    private static readonly TimeSpan Tolerance = TimeSpan.FromMilliseconds(300);

    [Fact(Timeout = 60_000)]
    public async Task SleepUntil_a_time_that_has_passed_continues_right_away()
    {
        var run = await RunToEndAsync(SleepFunctions.Past, new SleepUntilEvent(TestId, DateTimeOffset.UtcNow.AddHours(-1)));

        run.Status.ShouldBe(RunStatus.Completed);
        var before = Observed.Named("before").ShouldHaveSingleItem().At;
        var after = Observed.Named("after").ShouldHaveSingleItem().At;
        (after - before).ShouldBeLessThan(TimeSpan.FromSeconds(10));
    }

    // The time is given with a +05:30 offset; waking hours early or late would show a time zone mistake.
    [Fact(Timeout = 60_000)]
    public async Task SleepUntil_a_time_given_in_another_time_zone_wakes_at_that_instant()
    {
        var now = DateTimeOffset.UtcNow;
        var until = new DateTimeOffset(now.Ticks - (now.Ticks % TimeSpan.TicksPerSecond), TimeSpan.Zero).AddSeconds(3).ToOffset(new TimeSpan(5, 30, 0));

        var run = await RunToEndAsync(SleepFunctions.Until, new SleepUntilEvent(TestId, until), TimeSpan.FromSeconds(20));

        run.Status.ShouldBe(RunStatus.Completed);
        Observed.Named("woke").ShouldHaveSingleItem().At.ShouldBeInRange(until - TimeSpan.FromSeconds(1), until + TimeSpan.FromSeconds(15));
    }

    [Fact(Timeout = 60_000)]
    public async Task A_sleep_of_zero_length_continues_right_away()
    {
        var run = await RunToEndAsync(SleepFunctions.Zero, new RunEvent(TestId));

        run.Status.ShouldBe(RunStatus.Completed);
        (Observed.Named("after").ShouldHaveSingleItem().At - Observed.Named("before").ShouldHaveSingleItem().At).ShouldBeLessThan(TimeSpan.FromSeconds(10));
    }

    [Fact(Timeout = 60_000)]
    public async Task Parallel_sleeps_resume_the_run_once_the_longest_has_passed()
    {
        var run = await RunToEndAsync(SleepFunctions.Parallel, new RunEvent(TestId), TimeSpan.FromSeconds(20));

        run.Status.ShouldBe(RunStatus.Completed);
        var slept = Observed.Named("after").ShouldHaveSingleItem().At - Observed.Named("before").ShouldHaveSingleItem().At;
        slept.ShouldBeInRange(TimeSpan.FromSeconds(2) - Tolerance, TimeSpan.FromSeconds(12));
    }

    [Fact(Timeout = 60_000)]
    public async Task A_sleep_id_used_in_every_iteration_of_a_loop_sleeps_every_time()
    {
        var run = await RunToEndAsync(SleepFunctions.Loop, new RunEvent(TestId), TimeSpan.FromSeconds(25));

        run.Status.ShouldBe(RunStatus.Completed);
        run.OutputAs<string>().ShouldBe("rested");
        var ticks = Observed.Named("tick");
        ticks.Select(tick => (int)tick.Value!).ShouldBe([0, 1, 2]);
        (ticks[1].At - ticks[0].At).ShouldBeGreaterThan(TimeSpan.FromSeconds(1) - Tolerance);
        (ticks[2].At - ticks[1].At).ShouldBeGreaterThan(TimeSpan.FromSeconds(1) - Tolerance);
    }
}

[Trait("Category", "Evaluation")]
[Trait("Area", "Sleep and waitForEvent")]
public sealed class WaitForEventScenarios(EvaluationEnvironment environment) : EvaluationScenario<EvaluationDevFixture>(environment)
{
    [Fact(Timeout = 60_000)]
    public async Task Match_resumes_the_wait_only_with_an_event_that_has_the_same_field_value()
    {
        var orderId = $"order-{TestId}";
        var eventId = await SendAsync(WaitForEventFunctions.MatchStart, new WaitStart(TestId, OrderId: orderId));
        var run = await WaitForRunAsync(eventId);
        await WaitForStepAsync(run.RunId, StepOps.WaitForEvent);

        await SendAsync(WaitForEventFunctions.MatchResume, new Resume(TestId, "another order", OrderId: $"other-{TestId}"));
        var resumeId = await SendAsync(WaitForEventFunctions.MatchResume, new Resume(TestId, "this order", OrderId: orderId));
        var ended = await WaitForEndAsync(eventId);

        ended.Status.ShouldBe(RunStatus.Completed);
        ended.OutputAs<WaitOutcome>().ShouldBe(new WaitOutcome(true, "this order", resumeId));
    }

    [Fact(Timeout = 60_000)]
    public async Task If_resumes_the_wait_only_with_an_event_for_which_the_expression_holds()
    {
        var eventId = await SendAsync(WaitForEventFunctions.IfStart, new WaitStart(TestId));
        var run = await WaitForRunAsync(eventId);
        await WaitForStepAsync(run.RunId, StepOps.WaitForEvent);

        await SendAsync(WaitForEventFunctions.IfResume, new Resume(TestId, "too small", Amount: 50));
        await SendAsync(WaitForEventFunctions.IfResume, new Resume(NewTestId(), "someone else's", Amount: 500));
        await SendAsync(WaitForEventFunctions.IfResume, new Resume(TestId, "big enough", Amount: 150));
        var ended = await WaitForEndAsync(eventId);

        ended.OutputAs<WaitOutcome>().Message.ShouldBe("big enough");
    }

    [Fact(Timeout = 60_000)]
    public async Task A_wait_that_times_out_returns_null_and_the_run_goes_on()
    {
        var run = await RunToEndAsync(WaitForEventFunctions.TimeoutStart, new WaitStart(TestId), TimeSpan.FromSeconds(20));

        run.Status.ShouldBe(RunStatus.Completed);
        run.OutputAs<WaitOutcome>().ShouldBe(new WaitOutcome(false, null, null));
        var waited = Observed.Named("after").ShouldHaveSingleItem().At - Observed.Named("before").ShouldHaveSingleItem().At;
        waited.ShouldBeGreaterThan(WaitForEventFunctions.ShortTimeout - TimeSpan.FromMilliseconds(300));
    }

    // A wait only sees events that arrive after it starts listening. The early event is sent while the step before the
    // wait is held at a gate, and the gate opens a second later, once the server has long processed the event.
    [Fact(Timeout = 60_000)]
    public async Task An_event_sent_before_the_wait_starts_does_not_resume_it()
    {
        var eventId = await SendAsync(WaitForEventFunctions.EarlyStart, new WaitStart(TestId));
        var run = await WaitForRunAsync(eventId);
        await WaitForObservationsAsync("prepare");

        await SendAsync(WaitForEventFunctions.EarlyResume, new Resume(TestId, "early"));
        await Task.Delay(TimeSpan.FromSeconds(1), Cancellation);
        Gates.Open(TestId, WaitForEventFunctions.PreparedGate);
        await WaitForStepAsync(run.RunId, StepOps.WaitForEvent);
        await Task.Delay(TimeSpan.FromSeconds(1), Cancellation);
        (await Api.GetRunAsync(run.RunId, Cancellation)).HasEnded.ShouldBeFalse("the early event must not have resumed the wait");

        await SendAsync(WaitForEventFunctions.EarlyResume, new Resume(TestId, "late"));
        var ended = await WaitForEndAsync(eventId);

        ended.OutputAs<WaitOutcome>().Message.ShouldBe("late");
    }

    [Fact(Timeout = 60_000)]
    public async Task Parallel_waits_resume_independently_and_in_any_order()
    {
        var eventId = await SendAsync(WaitForEventFunctions.ParallelStart, new WaitStart(TestId));
        var run = await WaitForRunAsync(eventId);
        await WaitForStepAsync(run.RunId, StepOps.WaitForEvent, count: 2);

        // The second wait resolves first, so the next call replays one wait that is done and one that still waits.
        await SendAsync(WaitForEventFunctions.ParallelSecond, new Resume(TestId, "second arrived first"));
        await Api.WaitForTraceAsync(
            run.RunId,
            "only the first wait still waiting",
            trace => trace.Spans.Count(span => span.StepOp == StepOps.WaitForEvent && span.Status == SpanStatuses.Waiting) == 1,
            Cancellation);
        await SendAsync(WaitForEventFunctions.ParallelFirst, new Resume(TestId, "first arrived last"));
        var ended = await WaitForEndAsync(eventId);

        ended.Status.ShouldBe(RunStatus.Completed);
        ended.OutputAs<TwoWaits>().ShouldBe(new TwoWaits("first arrived last", "second arrived first"));
    }

    [Fact(Timeout = 60_000)]
    public async Task A_wait_that_times_out_and_one_that_matches_can_run_side_by_side()
    {
        var eventId = await SendAsync(WaitForEventFunctions.MixedStart, new WaitStart(TestId));
        var run = await WaitForRunAsync(eventId);
        await WaitForStepAsync(run.RunId, StepOps.WaitForEvent, count: 2);

        await SendAsync(WaitForEventFunctions.MixedArrives, new Resume(TestId, "arrived"));
        var ended = await WaitForEndAsync(eventId, TimeSpan.FromSeconds(20));

        ended.Status.ShouldBe(RunStatus.Completed);
        ended.OutputAs<TwoWaits>().ShouldBe(new TwoWaits(null, "arrived"));
    }

    [Fact(Timeout = 60_000)]
    public async Task One_event_resumes_every_run_that_waits_for_it()
    {
        var group = $"group-{TestId}";
        var ids = await SendManyAsync(
        [
            new EventPayload(WaitForEventFunctions.SharedStart, new WaitStart(TestId, Group: group)),
            new EventPayload(WaitForEventFunctions.SharedStart, new WaitStart(TestId, Group: group)),
        ]);
        foreach (var id in ids)
        {
            await WaitForStepAsync((await WaitForRunAsync(id)).RunId, StepOps.WaitForEvent);
        }

        var goId = await SendAsync(WaitForEventFunctions.SharedGo, new Resume(TestId, "go", Group: group));
        var runs = await Task.WhenAll(ids.Select(id => WaitForEndAsync(id)));

        runs.ShouldAllBe(run => run.Status == RunStatus.Completed);
        runs.Select(run => run.OutputAs<WaitOutcome>()).ShouldAllBe(outcome => outcome == new WaitOutcome(true, "go", goId));
    }

    [Fact(Timeout = 60_000)]
    public async Task The_awaited_event_arrives_with_its_name_id_timestamp_and_typed_data()
    {
        var eventId = await SendAsync(WaitForEventFunctions.TypedStart, new WaitStart(TestId));
        var run = await WaitForRunAsync(eventId);
        await WaitForStepAsync(run.RunId, StepOps.WaitForEvent);

        var timestamp = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        var shipment = new Shipment(TestId, "PPL", ["p-1", "p-2"], 3.25m);
        var client = Fixture.App.Services.GetRequiredService<IInngestClient>();
        await client.SendAsync(
            new InngestEvent(WaitForEventFunctions.TypedResume, shipment) { Id = $"shipment-{TestId}", Timestamp = timestamp },
            Cancellation);
        var ended = await WaitForEndAsync(eventId);

        var seen = ended.OutputAs<TypedWait>();
        seen.Name.ShouldBe(WaitForEventFunctions.TypedResume);
        seen.Id.ShouldBe($"shipment-{TestId}");
        seen.Timestamp.ShouldBe(timestamp);
        seen.Data.Carrier.ShouldBe("PPL");
        seen.Data.Parcels.ShouldBe(["p-1", "p-2"]);
        seen.Data.Weight.ShouldBe(3.25m);
    }

    private Gates Gates => Fixture.Gates;

    private static string NewTestId() => Guid.NewGuid().ToString("N");
}
