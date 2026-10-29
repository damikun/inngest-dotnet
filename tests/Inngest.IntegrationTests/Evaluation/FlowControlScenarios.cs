using Inngest.IntegrationTests.Evaluation.Functions;
using Inngest.IntegrationTests.Evaluation.Harness;
using Inngest.IntegrationTests.Harness;

namespace Inngest.IntegrationTests.Evaluation;

// Overlaps are measured from what the step bodies recorded on the app's clock, so the server's own timing cannot blur
// them: a limit is either kept or broken.
[Trait("Category", "Evaluation")]
[Trait("Area", "Flow control")]
public sealed class ConcurrencyScenarios(EvaluationEnvironment environment) : EvaluationScenario<EvaluationDevFixture>(environment)
{
    [Fact(Timeout = 60_000)]
    public async Task A_concurrency_limit_of_two_never_lets_more_than_two_steps_overlap()
    {
        var ids = await SendManyAsync(Enumerable.Range(1, 6).Select(value => new EventPayload(ConcurrencyFunctions.Limit, new ValueEvent(TestId, value))));

        var runs = await Task.WhenAll(ids.Select(id => WaitForEndAsync(id, TimeSpan.FromSeconds(30))));

        runs.ShouldAllBe(run => run.Status == RunStatus.Completed);
        Intervals.MaxOverlap(Observed.Values<Interval>("work")).ShouldBe(2);
    }

    [Fact(Timeout = 60_000)]
    public async Task A_keyed_concurrency_limit_applies_to_each_key_on_its_own()
    {
        var ids = await SendManyAsync(
            from round in Enumerable.Range(1, 3)
            from @group in new[] { "red", "blue" }
            select new EventPayload(ConcurrencyFunctions.Keyed, new GroupEvent(TestId, @group, round)));

        await Task.WhenAll(ids.Select(id => WaitForEndAsync(id, TimeSpan.FromSeconds(30))));

        var work = Observed.Values<GroupInterval>("work");
        work.Count.ShouldBe(6);
        Intervals.MaxOverlap(work.Where(item => item.Group == "red").Select(item => item.Interval)).ShouldBe(1);
        Intervals.MaxOverlap(work.Where(item => item.Group == "blue").Select(item => item.Interval)).ShouldBe(1);
        Intervals.MaxOverlap(work.Select(item => item.Interval)).ShouldBe(2);
    }

    [Fact(Timeout = 60_000)]
    public async Task A_concurrency_limit_also_holds_back_the_parallel_steps_of_one_run()
    {
        var run = await RunToEndAsync(ConcurrencyFunctions.Steps, new RunEvent(TestId), TimeSpan.FromSeconds(30));

        run.Status.ShouldBe(RunStatus.Completed);
        var parts = Observed.Values<Interval>("part");
        parts.Count.ShouldBe(3);
        Intervals.MaxOverlap(parts).ShouldBe(1);
    }

    [Fact(Timeout = 60_000)]
    public async Task A_sleeping_run_does_not_hold_a_concurrency_slot()
    {
        var sleeperId = await SendAsync(ConcurrencyFunctions.Sleeper, new RoleEvent(TestId, "sleeper", Seconds: 10));
        var sleeper = await WaitForRunAsync(sleeperId);
        await WaitForStepAsync(sleeper.RunId, StepOps.Sleep);

        var quick = await RunToEndAsync(ConcurrencyFunctions.Sleeper, new RoleEvent(TestId, "quick"));
        await WaitForEndAsync(sleeperId, TimeSpan.FromSeconds(30));

        quick.Status.ShouldBe(RunStatus.Completed);
        Observed.Names.ShouldBe(["before-sleep", "quick", "after-sleep"]);
    }

    // Priority moves a run ahead of runs queued up to that many seconds before it, so with the only slot taken the
    // high-priority run, sent last, starts before the one queued earlier.
    [Fact(Timeout = 60_000)]
    public async Task A_higher_priority_run_starts_ahead_of_runs_queued_before_it()
    {
        var holder = await SendAsync(ConcurrencyFunctions.Prioritized, new RoleEvent(TestId, "holder", Seconds: 2));
        await WaitForObservationsAsync("started");
        var low = await SendAsync(ConcurrencyFunctions.Prioritized, new RoleEvent(TestId, "low"));
        await Task.Delay(TimeSpan.FromMilliseconds(300), Cancellation);
        var high = await SendAsync(ConcurrencyFunctions.Prioritized, new RoleEvent(TestId, "high"));

        await Task.WhenAll(WaitForEndAsync(holder), WaitForEndAsync(low, TimeSpan.FromSeconds(30)), WaitForEndAsync(high, TimeSpan.FromSeconds(30)));

        Observed.Values<string>("started").ShouldBe(["holder", "high", "low"]);
    }
}

[Trait("Category", "Evaluation")]
[Trait("Area", "Flow control")]
public sealed class RateScenarios(EvaluationEnvironment environment) : EvaluationScenario<EvaluationDevFixture>(environment)
{
    // Throttling queues runs instead of dropping them and spaces their starts a period apart. The server's limiter
    // (GCRA) measures periods on its own clock and may start a run slightly early, so the bound allows a second less
    // than the two periods three starts need.
    [Fact(Timeout = 60_000)]
    public async Task A_throttle_queues_runs_and_spreads_their_starts_over_the_period()
    {
        var ids = await SendManyAsync(Enumerable.Range(1, 3).Select(value => new EventPayload(RateFunctions.Throttled, new ValueEvent(TestId, value))));

        var runs = await Task.WhenAll(ids.Select(id => WaitForEndAsync(id, TimeSpan.FromSeconds(30))));

        runs.ShouldAllBe(run => run.Status == RunStatus.Completed);
        var starts = Observed.Named("started").Select(start => start.At).Order().ToArray();
        starts.Length.ShouldBe(3);
        (starts[2] - starts[0]).ShouldBeGreaterThan((RateFunctions.ThrottlePeriod * 2) - TimeSpan.FromSeconds(1));
    }

    [Fact(Timeout = 60_000)]
    public async Task A_rate_limit_skips_the_events_beyond_its_limit()
    {
        var ids = await SendManyAsync(Enumerable.Range(1, 3).Select(value => new EventPayload(RateFunctions.RateLimited, new ValueEvent(TestId, value))));

        await WaitForObservationsAsync("ran");
        await Task.Delay(NoRunWindow, Cancellation);

        Observed.Named("ran").Count.ShouldBe(1);
        var runs = await Task.WhenAll(ids.Select(id => Api.GetEventRunsAsync(id, Cancellation)));
        runs.Sum(list => list.Count).ShouldBe(1);
    }

    [Fact(Timeout = 60_000)]
    public async Task Idempotency_runs_the_function_once_per_key()
    {
        await SendManyAsync(
        [
            new EventPayload(RateFunctions.Idempotent, new OrderEvent(TestId, "a")),
            new EventPayload(RateFunctions.Idempotent, new OrderEvent(TestId, "a")),
            new EventPayload(RateFunctions.Idempotent, new OrderEvent(TestId, "b")),
        ]);

        await WaitForObservationsAsync("ran", count: 2);
        await Task.Delay(NoRunWindow, Cancellation);

        Observed.Values<string>("ran").ShouldBe(["a", "b"], ignoreOrder: true);
    }
}

[Trait("Category", "Evaluation")]
[Trait("Area", "Flow control")]
public sealed class DebounceAndBatchScenarios(EvaluationEnvironment environment) : EvaluationScenario<EvaluationDevFixture>(environment)
{
    // The server measures the quiet period from when it received the last event, which is before the send returned. The
    // upper bound leaves room for a call the server has to retry.
    [Fact(Timeout = 60_000)]
    public async Task Debounce_runs_once_with_the_last_event_after_a_quiet_period()
    {
        DateTimeOffset lastSent = default;
        foreach (var value in new[] { 1, 2, 3 })
        {
            await SendAsync(DebounceAndBatchFunctions.Debounced, new ValueEvent(TestId, value));
            lastSent = DateTimeOffset.UtcNow;
            await Task.Delay(TimeSpan.FromMilliseconds(300), Cancellation);
        }

        var ran = (await WaitForObservationsAsync("ran", timeout: TimeSpan.FromSeconds(20))).ShouldHaveSingleItem();
        await Task.Delay(DebounceAndBatchFunctions.DebouncePeriod + TimeSpan.FromSeconds(0.5), Cancellation);

        Observed.Named("ran").Count.ShouldBe(1);
        ran.Value.ShouldBe(3);
        (ran.At - lastSent).ShouldBeInRange(DebounceAndBatchFunctions.DebouncePeriod - TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(12));
    }

    // Events arrive every second for six seconds, each one restarting the two second period, so without the four second
    // timeout there would be a single run, with the last event. Exactly when the timeout fires, and which event that run
    // gets, depends on the server's timing, so the assertions only pin down what the timeout guarantees.
    [Fact(Timeout = 60_000)]
    public async Task A_debounce_timeout_starts_a_run_even_while_events_keep_arriving()
    {
        for (var value = 1; value <= 7; value++)
        {
            await SendAsync(DebounceAndBatchFunctions.DebouncedWithTimeout, new ValueEvent(TestId, value));
            if (value < 7)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), Cancellation);
            }
        }

        await Poll.UntilAsync(
            "the run for the last event",
            _ => Observed.Values<int>("ran"),
            values => values.Contains(7),
            TimeSpan.FromSeconds(15),
            Cancellation);
        await Task.Delay(TimeSpan.FromSeconds(1), Cancellation);

        var runs = Observed.Values<int>("ran");
        runs.Count.ShouldBeInRange(2, 3);
        runs[0].ShouldBeLessThan(7);
        runs[^1].ShouldBe(7);
    }

    [Fact(Timeout = 60_000)]
    public async Task A_batch_runs_the_function_once_with_every_event_when_it_fills_up()
    {
        var sent = DateTimeOffset.UtcNow;
        await SendManyAsync(Enumerable.Range(1, 3).Select(value => new EventPayload(DebounceAndBatchFunctions.Batched, new ValueEvent(TestId, value))));

        var batch = (await WaitForObservationsAsync("batch", timeout: TimeSpan.FromSeconds(20))).ShouldHaveSingleItem();
        await Task.Delay(NoRunWindow, Cancellation);

        Observed.Named("batch").Count.ShouldBe(1);
        ((int[])batch.Value!).ShouldBe([1, 2, 3], ignoreOrder: true);

        // A full batch does not wait for its thirty second timeout.
        (batch.At - sent).ShouldBeLessThan(TimeSpan.FromSeconds(15));
    }

    [Fact(Timeout = 60_000)]
    public async Task A_batch_that_does_not_fill_up_runs_when_its_timeout_passes()
    {
        var sent = DateTimeOffset.UtcNow;
        await SendManyAsync(Enumerable.Range(1, 2).Select(value => new EventPayload(DebounceAndBatchFunctions.BatchedWithTimeout, new ValueEvent(TestId, value))));

        var batch = (await WaitForObservationsAsync("batch", timeout: TimeSpan.FromSeconds(20))).ShouldHaveSingleItem();

        ((int[])batch.Value!).ShouldBe([1, 2], ignoreOrder: true);
        (batch.At - sent).ShouldBeGreaterThan(DebounceAndBatchFunctions.BatchTimeout - TimeSpan.FromMilliseconds(500));
    }
}

[Trait("Category", "Evaluation")]
[Trait("Area", "Flow control")]
public sealed class SingletonAndTimeoutScenarios(EvaluationEnvironment environment) : EvaluationScenario<EvaluationDevFixture>(environment)
{
    [Fact(Timeout = 60_000)]
    public async Task Singleton_skip_drops_new_runs_while_one_with_the_same_key_is_active()
    {
        var first = await SendAsync(SingletonAndTimeoutFunctions.Skipping, new OrderEvent(TestId, "first", Seconds: 3));
        await WaitForStepAsync((await WaitForRunAsync(first)).RunId, StepOps.Sleep);

        var skipped = await SendAsync(SingletonAndTimeoutFunctions.Skipping, new OrderEvent(TestId, "skipped"));
        await WaitForEndAsync(first, TimeSpan.FromSeconds(20));
        var afterwards = await RunToEndAsync(SingletonAndTimeoutFunctions.Skipping, new OrderEvent(TestId, "afterwards"));

        afterwards.Status.ShouldBe(RunStatus.Completed);
        Observed.Values<string>("start").ShouldBe(["first", "afterwards"]);
        (await WaitForRunStatusesAsync(skipped)).ShouldBe(["SKIPPED"]);
    }

    [Fact(Timeout = 60_000)]
    public async Task Singleton_cancel_replaces_the_active_run_with_the_new_one()
    {
        var first = await SendAsync(SingletonAndTimeoutFunctions.Cancelling, new OrderEvent(TestId, "first", Seconds: 30));
        await WaitForStepAsync((await WaitForRunAsync(first)).RunId, StepOps.Sleep);

        var second = await SendAsync(SingletonAndTimeoutFunctions.Cancelling, new OrderEvent(TestId, "second", Seconds: 1));
        var cancelled = await WaitForEndAsync(first);
        var replacement = await WaitForEndAsync(second);

        cancelled.Status.ShouldBe(RunStatus.Cancelled);
        replacement.Status.ShouldBe(RunStatus.Completed);
        Observed.Values<string>("start").ShouldBe(["first", "second"]);
        Observed.Values<string>("end").ShouldBe(["second"]);
    }

    // The holder takes the only slot for five seconds; the waiter may wait two seconds in the queue, so it is
    // cancelled without ever running.
    [Fact(Timeout = 60_000)]
    public async Task A_start_timeout_cancels_a_run_that_waits_too_long_in_the_queue()
    {
        var holder = await SendAsync(SingletonAndTimeoutFunctions.StartTimeout, new RoleEvent(TestId, "holder", Seconds: 5));
        await WaitForObservationsAsync("started");
        var waiter = await SendAsync(SingletonAndTimeoutFunctions.StartTimeout, new RoleEvent(TestId, "waiter"));

        var waiterRun = await WaitForEndAsync(waiter, TimeSpan.FromSeconds(20));
        var holderRun = await WaitForEndAsync(holder, TimeSpan.FromSeconds(20));

        waiterRun.Status.ShouldBe(RunStatus.Cancelled);
        holderRun.Status.ShouldBe(RunStatus.Completed);
        Observed.Values<string>("started").ShouldBe(["holder"]);
    }

    // The timeout runs from the first attempt, delivered or not, so the run ends long before its twenty second sleep
    // would, and the step after the sleep never runs.
    [Fact(Timeout = 60_000)]
    public async Task A_finish_timeout_cancels_a_run_that_takes_too_long()
    {
        var run = await RunToEndAsync(SingletonAndTimeoutFunctions.FinishTimeout, new OrderEvent(TestId, "slow", Seconds: 20), TimeSpan.FromSeconds(30));

        run.Status.ShouldBe(RunStatus.Cancelled);
        Observed.Named("end").ShouldBeEmpty();
        (run.EndedAt!.Value - run.StartedAt!.Value).ShouldBeLessThan(TimeSpan.FromSeconds(15));
    }
}

[Trait("Category", "Evaluation")]
[Trait("Area", "Flow control")]
public sealed class CancellationScenarios(EvaluationEnvironment environment) : EvaluationScenario<EvaluationDevFixture>(environment)
{
    [Fact(Timeout = 60_000)]
    public async Task CancelOn_with_an_expression_cancels_only_when_it_holds()
    {
        var eventId = await SendAsync(CancellationFunctions.ByExpression, new OrderEvent(TestId, "order", Seconds: 30));
        var run = await WaitForRunAsync(eventId);
        await WaitForStepAsync(run.RunId, StepOps.Sleep);

        await SendAsync(CancellationFunctions.ByExpressionStop, new StopEvent(TestId, Reason: "pause"));
        await SendAsync(CancellationFunctions.ByExpressionStop, new StopEvent(Guid.NewGuid().ToString("N")));
        await Task.Delay(NoRunWindow, Cancellation);
        (await Api.GetRunAsync(run.RunId, Cancellation)).HasEnded.ShouldBeFalse("events that do not match the expression must not cancel the run");

        await SendAsync(CancellationFunctions.ByExpressionStop, new StopEvent(TestId));
        var ended = await WaitForEndAsync(eventId);

        ended.Status.ShouldBe(RunStatus.Cancelled);
        Observed.Named("end").ShouldBeEmpty();
    }

    [Fact(Timeout = 60_000)]
    public async Task CancelOn_with_a_match_cancels_only_the_run_with_the_same_field()
    {
        var doomed = await SendAsync(CancellationFunctions.ByMatch, new OrderEvent(TestId, $"doomed-{TestId}", Seconds: 30));
        var spared = await SendAsync(CancellationFunctions.ByMatch, new OrderEvent(TestId, $"spared-{TestId}", Seconds: 3));
        await WaitForStepAsync((await WaitForRunAsync(doomed)).RunId, StepOps.Sleep);
        await WaitForStepAsync((await WaitForRunAsync(spared)).RunId, StepOps.Sleep);

        await SendAsync(CancellationFunctions.ByMatchStop, new StopEvent(TestId, OrderId: $"doomed-{TestId}"));

        (await WaitForEndAsync(doomed)).Status.ShouldBe(RunStatus.Cancelled);
        (await WaitForEndAsync(spared)).Status.ShouldBe(RunStatus.Completed);
        Observed.Values<string>("end").ShouldBe([$"spared-{TestId}"]);
    }

    // The cancel event arrives two seconds after the run was scheduled, a second after the window closed.
    [Fact(Timeout = 60_000)]
    public async Task CancelOn_with_a_timeout_stops_listening_once_it_passes()
    {
        var sent = DateTimeOffset.UtcNow;
        var eventId = await SendAsync(CancellationFunctions.Windowed, new OrderEvent(TestId, "order", Seconds: 4));
        await WaitForStepAsync((await WaitForRunAsync(eventId)).RunId, StepOps.Sleep);
        var late = sent + (CancellationFunctions.Window * 2) - DateTimeOffset.UtcNow;
        if (late > TimeSpan.Zero)
        {
            await Task.Delay(late, Cancellation);
        }

        await SendAsync(CancellationFunctions.WindowedStop, new StopEvent(TestId));
        var ended = await WaitForEndAsync(eventId, TimeSpan.FromSeconds(20));

        ended.Status.ShouldBe(RunStatus.Completed);
        Observed.Values<string>("end").ShouldBe(["order"]);
    }
}
