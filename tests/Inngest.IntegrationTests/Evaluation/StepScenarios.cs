using System.Text.Json;
using Inngest.IntegrationTests.Evaluation.Functions;
using Inngest.IntegrationTests.Evaluation.Harness;
using Inngest.IntegrationTests.Harness;

namespace Inngest.IntegrationTests.Evaluation;

[Trait("Category", "Evaluation")]
[Trait("Area", "Steps")]
public sealed class StepScenarios(EvaluationEnvironment environment) : EvaluationScenario<EvaluationDevFixture>(environment)
{
    [Fact(Timeout = 60_000)]
    public async Task Twenty_steps_in_a_row_each_run_once_and_in_order()
    {
        var run = await RunToEndAsync(StepFunctions.Chain, new ValueEvent(TestId, 20), TimeSpan.FromSeconds(30));

        run.Status.ShouldBe(RunStatus.Completed);
        run.OutputAs<ChainResult>().ShouldBe(new ChainResult(20, 210));
        Observed.Names.ShouldBe([.. Enumerable.Range(1, 20).Select(index => $"step-{index}")]);
    }

    [Fact(Timeout = 60_000)]
    public async Task A_step_id_used_in_every_iteration_of_a_loop_keeps_one_result_per_iteration()
    {
        var run = await RunToEndAsync(StepFunctions.Loop, new RunEvent(TestId));

        run.Status.ShouldBe(RunStatus.Completed);
        run.OutputAs<int[]>().ShouldBe([0, 1, 4, 9, 16]);
        Observed.Values<int>("square").ShouldBe([0, 1, 2, 3, 4]);
    }

    [Fact(Timeout = 60_000)]
    public async Task An_explicit_step_id_that_looks_like_a_repeat_suffix_does_not_collide_with_one()
    {
        var run = await RunToEndAsync(StepFunctions.SuffixIds, new RunEvent(TestId));

        run.Status.ShouldBe(RunStatus.Completed);
        run.OutputAs<string[]>().ShouldBe(["explicit", "first", "second"]);
        Observed.Names.ShouldBe(["explicit", "first", "second"]);
    }

    // Code outside steps runs again on every call, so it has to wait for its own I/O each time without the SDK taking
    // the pause for the end of the function.
    [Fact(Timeout = 60_000)]
    public async Task Code_between_steps_can_await_timers_files_and_http_calls()
    {
        var run = await RunToEndAsync(StepFunctions.Io, new RunEvent(TestId));

        run.Status.ShouldBe(RunStatus.Completed);
        run.OutputAs<IoResult>().ShouldBe(new IoResult("A", "A written then B", "A written", 200));
        Observed.Named("a").Count.ShouldBe(1);
        Observed.Named("b").Count.ShouldBe(1);
        Observed.Named("between").Count.ShouldBeGreaterThanOrEqualTo(2);
    }

    [Fact(Timeout = 60_000)]
    public async Task A_step_that_fails_all_its_attempts_can_be_caught_and_compensated()
    {
        var run = await RunToEndAsync(StepFunctions.Catch, new RunEvent(TestId));

        run.Status.ShouldBe(RunStatus.Completed);
        run.OutputAs<CaughtResult>().ShouldBe(new CaughtResult(true, "charge", nameof(InvalidOperationException), "card declined", "refunded"));
        Observed.Values<int>("charge").ShouldClimbTo(1);
        Observed.Named("refund").Count.ShouldBe(1);
    }

    [Fact(Timeout = 60_000)]
    public async Task A_step_that_throws_NonRetriableException_fails_at_once_and_can_be_caught()
    {
        var run = await RunToEndAsync(StepFunctions.CatchNonRetriable, new RunEvent(TestId));

        run.Status.ShouldBe(RunStatus.Completed);
        run.OutputAs<CaughtResult>().ShouldBe(new CaughtResult(true, "validate", nameof(NonRetriableException), "the order is invalid", null));
        Observed.Named("validate").Count.ShouldBe(1);
    }

    [Fact(Timeout = 60_000)]
    public async Task An_async_step_handler_awaits_its_own_work_before_the_result_is_saved()
    {
        var run = await RunToEndAsync(StepFunctions.AsyncHandler, new RunEvent(TestId));

        run.Status.ShouldBe(RunStatus.Completed);
        var result = run.OutputAs<AsyncStepResult>();
        result.Text.ShouldBe("computed off the request thread");
        result.TokenCanceled.ShouldBeFalse();
        result.ElapsedMilliseconds.ShouldBeGreaterThanOrEqualTo(290);
        Observed.Named("work").Count.ShouldBe(1);
    }

    // The SDK reports a step that was started but never awaited instead of the return value, so it still runs, once.
    [Fact(Timeout = 60_000)]
    public async Task A_step_started_but_never_awaited_still_runs_once()
    {
        var run = await RunToEndAsync(StepFunctions.Unawaited, new RunEvent(TestId));

        run.Status.ShouldBe(RunStatus.Completed);
        run.OutputAs<string>().ShouldBe("returned without waiting");
        await Task.Delay(NoRunWindow, Cancellation);
        Observed.Named("side-effect").Count.ShouldBe(1);
    }

    [Fact(Timeout = 60_000)]
    public async Task The_function_output_can_be_any_json_value_or_nothing()
    {
        (string Kind, string? Json)[] cases = [("text", "\"plain text\""), ("number", "42.5"), ("array", "[1,2,3]"), ("element", """{"raw":true}"""), ("null", null)];

        var runs = await Task.WhenAll(cases.Select(@case => RunToEndAsync(StepFunctions.OutputKind, new OutputKindEvent(TestId, @case.Kind))));

        runs.ShouldAllBe(run => run.Status == RunStatus.Completed);
        foreach (var (run, (kind, json)) in runs.Zip(cases))
        {
            if (json is null)
            {
                run.Output.ShouldBeNull(kind);
                continue;
            }

            using var expected = JsonDocument.Parse(json);
            JsonElement.DeepEquals(run.Output.ShouldNotBeNull(kind), expected.RootElement).ShouldBeTrue($"{kind}: {run.Output?.GetRawText()}");
        }
    }
}

[Trait("Category", "Evaluation")]
[Trait("Area", "Steps")]
public sealed class StepDataScenarios(EvaluationEnvironment environment) : EvaluationScenario<EvaluationDevFixture>(environment)
{
    [Fact(Timeout = 60_000)]
    public async Task A_step_that_returns_null_is_memoized_and_not_run_again()
    {
        var run = await RunToEndAsync(StepDataFunctions.Null, new RunEvent(TestId));

        run.Status.ShouldBe(RunStatus.Completed);
        run.OutputAs<NullResult>().ShouldBe(new NullResult(true, true, null));
        Observed.Names.ShouldBe(["null-text", "null-record", "null-number"]);
    }

    [Fact(Timeout = 60_000)]
    public async Task Primitive_step_results_come_back_exactly_as_returned()
    {
        var run = await RunToEndAsync(StepDataFunctions.PrimitiveValues, new RunEvent(TestId));

        run.Status.ShouldBe(RunStatus.Completed);
        var output = run.Output.ShouldNotBeNull();
        output.GetProperty("textEqual").GetBoolean().ShouldBeTrue();
        output.GetProperty("timeEqual").GetBoolean().ShouldBeTrue();
        output.GetProperty("guidEqual").GetBoolean().ShouldBeTrue();
        output.GetProperty("allEqual").GetBoolean().ShouldBeTrue(output.GetProperty("all").GetRawText());
        output.GetProperty("untyped").Deserialize<string[]>().ShouldBe(["2147483647", "9007199254740991", "0.30000000000000004", "True"]);
        output.GetProperty("all").Deserialize<Primitives>(JsonSerializerOptions.Web).ShouldBe(StepDataFunctions.SamplePrimitives);
    }

    [Fact(Timeout = 60_000)]
    public async Task A_nested_record_step_result_comes_back_intact_in_later_steps()
    {
        var run = await RunToEndAsync(StepDataFunctions.Record, new RunEvent(TestId));

        run.Status.ShouldBe(RunStatus.Completed);
        var output = run.Output.ShouldNotBeNull();
        output.GetProperty("sameAsReturned").GetBoolean().ShouldBeTrue();
        output.GetProperty("total").GetDecimal().ShouldBe(139.98m);
        output.GetProperty("order").ShouldContainJson("""
            {
              "id": "order-1", "state": 2,
              "shipTo": { "street": "Main Street 1", "city": "Prague", "region": null },
              "lines": [{ "sku": "sku-1", "quantity": 2, "price": 9.99 }, { "sku": "sku-2", "quantity": 1, "price": 120 }],
              "tags": { "channel": "web", "coupon": "" },
              "matrix": [[1, 2], [3], []],
              "previous": { "id": "order-0", "previous": null }
            }
            """);
        Observed.Named("load-order").Count.ShouldBe(1);
    }

    [Fact(Timeout = 60_000)]
    public async Task A_one_megabyte_step_result_comes_back_byte_for_byte()
    {
        const int Length = 1024 * 1024;

        var run = await RunToEndAsync(StepDataFunctions.Large, new ValueEvent(TestId, Length), TimeSpan.FromSeconds(30));

        run.Status.ShouldBe(RunStatus.Completed);
        run.OutputAs<LargeResult>().ShouldBe(new LargeResult(Length, StepDataFunctions.Sha256(StepDataFunctions.LargeText(TestId, Length))));
        Observed.Named("large").Count.ShouldBe(1);
    }

    [Fact(Timeout = 60_000)]
    public async Task Unicode_data_and_unicode_step_ids_round_trip()
    {
        const string Text = "Příliš žluťoučký kůň 🐎 漢字 עברית العربية e\u0301 👩‍👩‍👧 \"quotes\" \\ / \t\n\u0001 \uFEFF end";

        var run = await RunToEndAsync(StepDataFunctions.Unicode, new UnicodeEvent(TestId, Text));

        run.Status.ShouldBe(RunStatus.Completed);
        run.OutputAs<UnicodeResult>().ShouldBe(new UnicodeResult(Text, $"{Text}|{Text.Length}", Text.Length));
        Observed.Names.ShouldBe(["first", "second"]);
    }
}

[Trait("Category", "Evaluation")]
[Trait("Area", "Steps")]
public sealed class ParallelStepScenarios(EvaluationEnvironment environment) : EvaluationScenario<EvaluationDevFixture>(environment)
{
    [Theory(Timeout = 60_000)]
    [InlineData(10)]
    [InlineData(50)]
    public async Task Parallel_steps_fanned_out_with_WhenAll_each_run_once(int count)
    {
        var run = await RunToEndAsync(ParallelStepFunctions.FanOut, new ValueEvent(TestId, count), TimeSpan.FromSeconds(40));

        run.Status.ShouldBe(RunStatus.Completed);
        var expected = Enumerable.Range(1, count).Select(index => index * 10).ToArray();
        run.OutputAs<FanOutResult>().ShouldBe(new FanOutResult(expected, expected.Sum()), new FanOutComparer());
        Observed.Values<int>("part").ShouldBe(Enumerable.Range(1, count), ignoreOrder: true);
    }

    // The slow step is held at a gate until the fast one has finished and its result has had time to reach Inngest.
    [Fact(Timeout = 60_000)]
    public async Task WhenAny_resolves_with_the_step_that_finished_first_on_every_replay()
    {
        var eventId = await SendAsync(ParallelStepFunctions.Race, new RunEvent(TestId));
        await WaitForObservationsAsync("fast");
        await Task.Delay(TimeSpan.FromMilliseconds(500), Cancellation);
        Fixture.Gates.Open(TestId, ParallelStepFunctions.SlowGate);
        var run = await WaitForEndAsync(eventId, TimeSpan.FromSeconds(30));

        run.Status.ShouldBe(RunStatus.Completed);
        run.OutputAs<RaceResult>().ShouldBe(new RaceResult("fast", "fast won"));
        Observed.Named("fast").Count.ShouldBe(1);
        Observed.Named("slow").Count.ShouldBe(1);
        Observed.Values<string>("after").ShouldBe(["fast"]);
    }

    [Fact(Timeout = 60_000)]
    public async Task Code_that_leaves_the_scheduler_still_runs_every_step_once()
    {
        var run = await RunToEndAsync(ParallelStepFunctions.ConfigureAwaitFalse, new RunEvent(TestId));

        run.Status.ShouldBe(RunStatus.Completed);
        run.OutputAs<ConfigureAwaitResult>().ShouldBe(
            new ConfigureAwaitResult(1, 2, [3, 4], "from the thread pool"),
            new ConfigureAwaitComparer());
        Observed.Names.Order().ShouldBe(["a", "b", "c", "d", "e"]);
    }

    [Fact(Timeout = 60_000)]
    public async Task Rounds_of_parallel_steps_in_a_loop_feed_each_other()
    {
        var run = await RunToEndAsync(ParallelStepFunctions.Rounds, new RunEvent(TestId), TimeSpan.FromSeconds(30));

        run.Status.ShouldBe(RunStatus.Completed);
        run.OutputAs<int[]>().ShouldBe([6, 36, 216]);
        Observed.Values<string>("part").Order().ShouldBe(["1.1", "1.2", "1.3", "2.1", "2.2", "2.3", "3.1", "3.2", "3.3"]);
    }

    [Fact(Timeout = 60_000)]
    public async Task Parallel_branches_with_steps_in_sequence_each_run_every_step_once()
    {
        var run = await RunToEndAsync(ParallelStepFunctions.Branches, new RunEvent(TestId));

        run.Status.ShouldBe(RunStatus.Completed);
        run.OutputAs<BranchesResult>().ShouldBe(new BranchesResult("left1+left2", "right1+right2"));
        var names = Observed.Names.ToList();
        names.Order().ShouldBe(["left-1", "left-2", "right-1", "right-2"]);
        names.IndexOf("left-1").ShouldBeLessThan(names.IndexOf("left-2"));
        names.IndexOf("right-1").ShouldBeLessThan(names.IndexOf("right-2"));
    }

    private sealed class FanOutComparer : IEqualityComparer<FanOutResult>
    {
        public bool Equals(FanOutResult? x, FanOutResult? y) =>
            x is not null && y is not null && x.Total == y.Total && x.Parts.SequenceEqual(y.Parts);

        public int GetHashCode(FanOutResult obj) => obj.Total;
    }

    private sealed class ConfigureAwaitComparer : IEqualityComparer<ConfigureAwaitResult>
    {
        public bool Equals(ConfigureAwaitResult? x, ConfigureAwaitResult? y) =>
            x is not null && y is not null && x.A == y.A && x.B == y.B && x.Parallel.SequenceEqual(y.Parallel) && x.FromThreadPool == y.FromThreadPool;

        public int GetHashCode(ConfigureAwaitResult obj) => obj.A;
    }
}
