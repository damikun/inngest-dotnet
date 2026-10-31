using Inngest.IntegrationTests.Evaluation.Functions;
using Inngest.IntegrationTests.Evaluation.Harness;
using Inngest.IntegrationTests.Harness;

namespace Inngest.IntegrationTests.FanOut;

// Lists fanned out as steps, against a real Inngest server.
public sealed class FanOutTests(EvaluationEnvironment environment) : EvaluationScenario<FanOutFixture>(environment)
{
    [Fact(Timeout = 60_000)]
    public async Task Items_run_a_window_at_a_time_and_the_results_come_back_in_the_order_of_the_items()
    {
        var run = await RunToEndAsync(FanOutFunctions.Window, new ValueEvent(TestId, 7), TimeSpan.FromSeconds(40));

        run.Status.ShouldBe(RunStatus.Completed);
        run.OutputAs<int[]>().ShouldBe([0, 1, 4, 9, 16, 25, 36]);

        // Every handler ran once, three together in a window and never more than three: the handlers of a window wait for
        // each other, so the run completes only if the window filled up.
        Observed.Values<int>("item").Order().ShouldBe([0, 1, 2, 3, 4, 5, 6]);
        Observed.Values<int>("running").Max().ShouldBe(3);

        // The server knows the steps by the ids of the items.
        var trace = await Api.GetRunTraceAsync(run.RunId, Cancellation);
        var names = trace!.Spans.Select(span => span.Name).ToArray();
        for (var i = 0; i < 7; i++)
        {
            names.ShouldContain($"square[{i}]");
        }
    }

    [Fact(Timeout = 60_000)]
    public async Task A_failing_item_fails_the_run_and_the_items_after_its_window_never_start()
    {
        var run = await RunToEndAsync(FanOutFunctions.FailFast, new ValueEvent(TestId, 6));

        run.Status.ShouldBe(RunStatus.Failed);
        run.Error.Message.ShouldBe("Card declined for item 1");
        Observed.Values<int>("item").Order().ShouldBe([0, 1]);
    }

    [Fact(Timeout = 60_000)]
    public async Task Outcomes_say_which_items_failed_and_every_item_runs()
    {
        var run = await RunToEndAsync(FanOutFunctions.Outcomes, new ValueEvent(TestId, 5));

        run.Status.ShouldBe(RunStatus.Completed);
        var outcomes = run.OutputAs<FanOutOutcome<int>[]>();
        outcomes.Select(outcome => outcome.Index).ShouldBe([0, 1, 2, 3, 4]);
        outcomes.Select(outcome => outcome.Succeeded).ShouldBe([true, false, true, false, true]);
        outcomes.Where(outcome => outcome.Succeeded).Select(outcome => outcome.Value).ShouldBe([0, 20, 40]);
        outcomes[1].Error!.Message.ShouldBe("Card declined for item 1");
        outcomes[3].Error!.Message.ShouldBe("Card declined for item 3");
        Observed.Values<int>("item").Order().ShouldBe([0, 1, 2, 3, 4]);
    }

    [Fact(Timeout = 60_000)]
    public async Task In_race_mode_the_window_moves_on_while_a_slow_item_is_still_running()
    {
        var eventId = await SendAsync(FanOutFunctions.Race, new RunEvent(TestId));

        // Items 1, 2 and 3 finish while item 0 waits at its gate.
        await WaitForObservationsAsync("item", count: 3);
        Observed.Values<int>("item").Order().ShouldBe([1, 2, 3]);

        Fixture.Gates.Open(TestId, FanOutFunctions.SlowGate);
        var run = await WaitForEndAsync(eventId, TimeSpan.FromSeconds(30));
        run.Status.ShouldBe(RunStatus.Completed);
        run.OutputAs<int[]>().ShouldBe([0, 10, 20, 30]);
        Observed.Values<int>("item").Count(item => item == 0).ShouldBe(1);
    }

    [Fact(Timeout = 60_000)]
    public async Task Without_race_mode_the_next_window_starts_once_the_whole_window_has_finished()
    {
        var eventId = await SendAsync(FanOutFunctions.Waves, new RunEvent(TestId));

        await WaitForObservationsAsync("item");
        Observed.Values<int>("item").ShouldBe([1]);

        // Inngest calls the function again only when item 0 has finished too, so items 2 and 3 have not started.
        await Task.Delay(TimeSpan.FromSeconds(3), Cancellation);
        Observed.Values<int>("start").Order().ShouldBe([0, 1]);

        Fixture.Gates.Open(TestId, FanOutFunctions.SlowGate);
        var run = await WaitForEndAsync(eventId, TimeSpan.FromSeconds(30));
        run.Status.ShouldBe(RunStatus.Completed);
        run.OutputAs<int[]>().ShouldBe([0, 10, 20, 30]);
    }

    [Fact(Timeout = 60_000)]
    public async Task In_race_mode_a_failure_fails_the_run_without_waiting_for_the_item_that_is_still_running()
    {
        var eventId = await SendAsync(FanOutFunctions.RaceFailure, new RunEvent(TestId));

        // Item 1 is still at its gate when the run fails, and items 2 and 3 never start.
        var run = await WaitForEndAsync(eventId, TimeSpan.FromSeconds(20));
        run.Status.ShouldBe(RunStatus.Failed);
        run.Error.Message.ShouldBe("Card declined for item 0");
        Observed.Values<int>("start").Order().ShouldBe([0, 1]);

        Fixture.Gates.Open(TestId, FanOutFunctions.SlowGate);
        await Task.Delay(TimeSpan.FromSeconds(1), Cancellation);
        Observed.Values<int>("start").Order().ShouldBe([0, 1]);
        (await Api.GetRunAsync(run.RunId, Cancellation)).Status.ShouldBe(RunStatus.Failed);
    }

    [Fact(Timeout = 150_000)]
    public async Task A_list_of_999_items_completes_and_one_of_1000_is_refused_before_a_step_runs_and_without_a_retry()
    {
        // Inngest fails a run once 1,000 of its steps have completed, so 1,000 items would run all their steps and then fail.
        var refused = await RunToEndAsync(FanOutFunctions.Limit, new ValueEvent(TestId, 1000));

        refused.Status.ShouldBe(RunStatus.Failed);
        refused.Error.Message!.ShouldContain("MapInChunksAsync");
        Observed.Names.ShouldBe(["call"]);

        var completed = await RunToEndAsync(FanOutFunctions.Limit, new ValueEvent(TestId, 999), TimeSpan.FromSeconds(100));

        completed.Status.ShouldBe(RunStatus.Completed);
        completed.OutputAs<int>().ShouldBe(999);
        Observed.Values<int>("item").Count.ShouldBe(999);
    }

    [Fact(Timeout = 90_000)]
    public async Task Chunks_run_as_child_runs_and_the_results_come_back_in_the_order_of_the_items()
    {
        var run = await RunToEndAsync(FanOutFunctions.Chunks, new ChunkedEvent(TestId, 25, 10, null), TimeSpan.FromSeconds(60));

        run.Status.ShouldBe(RunStatus.Completed);
        run.OutputAs<ChunkResult[]>().ShouldBe([.. Enumerable.Range(0, 25).Select(number => new ChunkResult(number, number * number))]);
        Observed.Values<int>("chunk-item").Order().ShouldBe([.. Enumerable.Range(0, 25)]);

        // The parent's steps are the three invokes. The items ran in the runs of the chunks.
        var trace = await Api.GetRunTraceAsync(run.RunId, Cancellation);
        var names = trace!.Spans.Select(span => span.Name).ToArray();
        names.ShouldContain("square[0-9]");
        names.ShouldContain("square[10-19]");
        names.ShouldContain("square[20-24]");
        names.ShouldNotContain("item[0]");
        trace.Spans.Where(span => span.StepOp == "INVOKE").Count().ShouldBe(3);
    }

    [Fact(Timeout = 90_000)]
    public async Task A_failing_item_in_a_chunk_fails_the_run_and_later_chunks_never_start()
    {
        var run = await RunToEndAsync(FanOutFunctions.Chunks, new ChunkedEvent(TestId, 25, 10, 13), TimeSpan.FromSeconds(60));

        run.Status.ShouldBe(RunStatus.Failed);
        run.Error.Message.ShouldBe("Item 13 was refused");

        // Chunks run one at a time here, so the third chunk (20 to 24) starts only if the second one succeeds.
        Observed.Values<int>("chunk-item").ShouldAllBe(number => number < 20);
    }

    [Fact(Timeout = 90_000)]
    public async Task Outcomes_of_chunks_collect_the_failures_and_run_every_item()
    {
        var run = await RunToEndAsync(FanOutFunctions.ChunkOutcomes, new ChunkedEvent(TestId, 25, 10, 13), TimeSpan.FromSeconds(60));

        run.Status.ShouldBe(RunStatus.Completed);
        var outcomes = run.OutputAs<FanOutOutcome<ChunkResult>[]>();
        outcomes.Select(outcome => outcome.Index).ShouldBe([.. Enumerable.Range(0, 25)]);
        outcomes.Where(outcome => !outcome.Succeeded).Select(outcome => outcome.Index).ShouldBe([13]);
        outcomes[13].Error!.Message.ShouldBe("Item 13 was refused");
        outcomes.Where(outcome => outcome.Succeeded).Select(outcome => outcome.Value!.Square)
            .ShouldBe([.. Enumerable.Range(0, 25).Where(number => number != 13).Select(number => number * number)]);
        Observed.Values<int>("chunk-item").Order().ShouldBe([.. Enumerable.Range(0, 25)]);
    }
}
