using Inngest.Functions;

namespace Inngest.Tests.Execution;

// Ports of the parallel cases in the TypeScript SDK's InngestFunction tests.
public sealed class ParallelismTests : IDisposable
{
    private readonly EngineHost _host = new();
    private readonly List<string> _ran = [];

    public void Dispose() => _host.Dispose();

    [Fact]
    public async Task Steps_started_together_are_planned_together()
    {
        var function = AllThenC();

        var result = await _host.CallAsync(function, Call.New());

        result.StatusCode.ShouldBe(206);
        result.Body.ShouldBe(
            """[{"id":"6dcd4ce23d88e2ee9568ba546c007c63d9131c1b","op":"StepPlanned","name":"A","displayName":"A","opts":{"type":"step.run"},"userland":{"id":"A"}},""" +
            """{"id":"ae4f281df5a5d0ff3cad6371f76d5c29b6d953ec","op":"StepPlanned","name":"B","displayName":"B","opts":{"type":"step.run"},"userland":{"id":"B"}}]""");
        _ran.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_targeted_call_runs_only_the_requested_step()
    {
        var function = AllThenC();

        var result = await _host.CallAsync(function, Call.New().DisableImmediateExecution(), stepId: Call.Hash("B"));

        result.OpCodes.ShouldBe(["StepRun"]);
        result.Op().GetProperty("id").GetString().ShouldBe(Call.Hash("B"));
        result.Op().GetProperty("data").GetString().ShouldBe("B");
        _ran.ShouldBe(["B"]);
    }

    [Fact]
    public async Task With_one_parallel_result_the_other_step_is_planned_again()
    {
        var function = AllThenC();

        var result = await _host.CallAsync(function, Call.New().Data("B", "\"B\"").DisableImmediateExecution());

        result.OpCodes.ShouldBe(["StepPlanned"]);
        result.DisplayNames.ShouldBe(["A"]);
        _ran.ShouldBeEmpty();
    }

    [Fact]
    public async Task After_the_parallel_steps_the_next_step_is_planned_when_immediate_execution_is_off()
    {
        var function = AllThenC();

        var result = await _host.CallAsync(function, Call.New().Data("B", "\"B\"").Data("A", "\"A\"").DisableImmediateExecution());

        result.OpCodes.ShouldBe(["StepPlanned"]);
        result.DisplayNames.ShouldBe(["C"]);
        _ran.ShouldBeEmpty();
    }

    [Fact]
    public async Task After_the_parallel_steps_the_next_step_runs_right_away_when_allowed()
    {
        var function = AllThenC();

        var result = await _host.CallAsync(function, Call.New().Data("B", "\"B\"").Data("A", "\"A\""));

        result.OpCodes.ShouldBe(["StepRun"]);
        result.DisplayNames.ShouldBe(["C"]);
        _ran.ShouldBe(["C"]);
    }

    [Fact]
    public async Task Parallel_steps_complete_the_run_once_all_are_memoized()
    {
        var function = AllThenC();

        var result = await _host.CallAsync(
            function,
            Call.New().Data("B", "\"B\"").Data("A", "\"A\"").Data("C", "\"C\"").DisableImmediateExecution());

        result.StatusCode.ShouldBe(200);
        result.Body.ShouldBe("\"ABC\"");
    }

    [Fact]
    public async Task A_race_reports_both_contenders_first()
    {
        var function = Race();

        var result = await _host.CallAsync(function, Call.New());

        result.OpCodes.ShouldBe(["StepPlanned", "StepPlanned"]);
        result.DisplayNames.ShouldBe(["A", "B"]);
    }

    [Fact]
    public async Task A_race_won_by_B_reports_the_loser_and_the_winner_branch()
    {
        var function = Race();

        var result = await _host.CallAsync(function, Call.New().Data("B", "\"B\"").DisableImmediateExecution());

        result.DisplayNames.ShouldBe(["A", "B wins"]);
    }

    [Fact]
    public async Task A_race_won_by_A_reports_the_loser_and_the_winner_branch()
    {
        var function = Race();

        var result = await _host.CallAsync(function, Call.New().Data("A", "\"A\"").DisableImmediateExecution());

        result.DisplayNames.ShouldBe(["B", "A wins"]);
    }

    [Fact]
    public async Task A_race_replays_its_winner_from_the_stack_and_reports_the_missing_loser()
    {
        var function = Race();

        var result = await _host.CallAsync(
            function,
            Call.New().Data("B", "\"B\"").Data("B wins", "\"B wins\"").DisableImmediateExecution());

        result.DisplayNames.ShouldBe(["A"]);
    }

    [Fact]
    public async Task A_race_completes_with_the_winner_it_first_had()
    {
        var function = Race();

        var result = await _host.CallAsync(
            function,
            Call.New().Data("A", "\"A\"").Data("B", "\"B\"").Data("B wins", "\"B wins\"").Stack("B", "B wins", "A"));

        result.StatusCode.ShouldBe(200);
        result.Body.ShouldBe("\"B wins\"");
    }

    [Fact]
    public async Task A_different_stack_order_replays_a_different_winner()
    {
        var function = Race();

        var result = await _host.CallAsync(
            function,
            Call.New().Data("A", "\"A\"").Data("B", "\"B\"").Data("A wins", "\"A wins\"").Stack("A", "A wins", "B"));

        result.StatusCode.ShouldBe(200);
        result.Body.ShouldBe("\"A wins\"");
    }

    [Fact]
    public async Task A_deep_race_won_by_the_chain_reports_the_loser_and_the_winner_branch()
    {
        var function = DeepRace();

        var result = await _host.CallAsync(
            function,
            Call.New().Data("B", "\"B\"").Data("B2", "\"B2\"").DisableImmediateExecution());

        result.OpCodes.ShouldBe(["StepPlanned", "StepPlanned"]);
        result.DisplayNames.ShouldBe(["A", "B wins"]);
    }

    [Fact]
    public async Task A_deep_race_follows_the_stack_when_the_loser_finished_later()
    {
        var function = DeepRace();

        var result = await _host.CallAsync(
            function,
            Call.New().Data("B", "\"B\"").Data("B2", "\"B2\"").Data("A", "\"A\"").Stack("B", "B2", "A").DisableImmediateExecution());

        result.DisplayNames.ShouldBe(["B wins"]);
        _host.Logs.GetSnapshot().ShouldNotContain(record => record.Id.Id == 100);
    }

    [Fact]
    public async Task A_deep_race_won_by_the_single_step_ignores_the_chain()
    {
        var function = DeepRace();

        var result = await _host.CallAsync(
            function,
            Call.New().Data("A", "\"A\"").Data("B", "\"B\"").Stack("A", "B").DisableImmediateExecution());

        result.DisplayNames.ShouldBe(["A wins", "B2"]);
    }

    [Fact]
    public async Task Repeated_ids_in_sequence_are_numbered_in_call_order()
    {
        var function = TestFunction.Define(async context =>
        {
            var values = new List<string>();
            for (var i = 0; i < 3; i++)
            {
                values.Add(await context.Step.RunAsync("A", () => Record($"A{i}")));
            }

            return string.Join(",", values);
        });

        var third = await _host.CallAsync(function, Call.New().Data("A", "\"A0\"").Data("A:1", "\"A1\""));
        var done = await _host.CallAsync(function, Call.New().Data("A", "\"A0\"").Data("A:1", "\"A1\"").Data("A:2", "\"A2\""));

        third.Body.ShouldBe(
            """[{"id":"8ac01954e36ad3a7e2472526fe50ebcbe1e59638","op":"StepRun","name":"A","displayName":"A","opts":{"type":"step.run"},"userland":{"id":"A","index":2},"data":"A2","timing":{"a":1793268000000000000,"b":0}}]""");
        done.Body.ShouldBe("\"A0,A1,A2\"");
        _ran.ShouldBe(["A2"]);
    }

    [Fact]
    public async Task Repeated_ids_started_together_are_numbered_in_call_order()
    {
        var function = TestFunction.Define(async context =>
        {
            await Task.WhenAll(
                context.Step.RunAsync("A", () => Record("first")),
                context.Step.RunAsync("A", () => Record("second")),
                context.Step.RunAsync("A", () => Record("third")));
            return null;
        });

        var result = await _host.CallAsync(function, Call.New());

        result.Ops.Select(op => op.GetProperty("id").GetString()).ShouldBe([Call.Hash("A"), Call.Hash("A:1"), Call.Hash("A:2")]);
        result.Ops.Select(op => op.GetProperty("name").GetString()).ShouldBe(["A", "A", "A"]);
        result.Ops[2].GetProperty("userland").GetRawText().ShouldBe("""{"id":"A","index":2}""");
    }

    [Fact]
    public async Task Reusing_an_id_after_other_work_warns_once()
    {
        var function = TestFunction.Define(async context =>
        {
            await context.Step.RunAsync("A", () => Record("A"));
            await Task.Delay(TimeSpan.FromSeconds(1), _host.Time);
            await Task.WhenAll(
                context.Step.RunAsync("A", () => Record("A again")),
                context.Step.RunAsync("A", () => Record("A third")));
            return null;
        });

        // Start returns once the engine waits for the delay, so the first use of the id is decided on by then.
        var pending = _host.Start(function, Call.New().Data("A", "\"A\""));
        _host.Time.Advance(TimeSpan.FromSeconds(1));
        var result = await EngineHost.ResultOf(pending);

        result.Ops.Select(op => op.GetProperty("id").GetString()).ShouldBe([Call.Hash("A:1"), Call.Hash("A:2")]);
        _host.Logs.GetSnapshot().Count(record => record.Id.Id == 101).ShouldBe(1);
    }

    [Fact]
    public async Task A_caught_step_failure_can_start_a_fallback_step()
    {
        var function = TestFunction.Define(async context =>
        {
            var results = await Task.WhenAll(
                context.Step.RunAsync("A", () => Record("A")),
                RunOrFallback(context.Step));
            return string.Join(",", results);
        });

        var fallback = await _host.CallAsync(
            function,
            Call.New().Data("A", "\"A\"").Failed("B", "Error", "B failed message").DisableImmediateExecution());
        var done = await _host.CallAsync(
            function,
            Call.New().Data("A", "\"A\"").Failed("B", "Error", "B failed message").Data("B failed", "\"B failed\"").DisableImmediateExecution());

        fallback.DisplayNames.ShouldBe(["B failed"]);
        done.Body.ShouldBe("\"A,B failed\"");
        _ran.ShouldBeEmpty();
    }

    [Fact]
    public async Task Steps_started_but_not_awaited_are_reported_instead_of_the_output()
    {
        var function = TestFunction.Define(async context =>
        {
            await context.Step.RunAsync("A", () => Record("A"));
            _ = context.Step.SleepAsync("floating", TimeSpan.FromHours(1));
            return "done";
        });

        var result = await _host.CallAsync(function, Call.New().Data("A", "\"A\""));

        result.StatusCode.ShouldBe(206);
        result.Body.ShouldBe(
            """[{"id":"f1c6f2a988096d2f732f80117b66826eddf315b9","op":"Sleep","name":"1h","displayName":"floating","opts":{"duration":"1h"},"userland":{"id":"floating"}}]""");
    }

    [Fact]
    public async Task Unawaited_steps_that_are_memoized_let_the_run_complete()
    {
        var function = TestFunction.Define(async context =>
        {
            await context.Step.RunAsync("A", () => Record("A"));
            _ = context.Step.SleepAsync("floating", TimeSpan.FromHours(1));
            return "done";
        });

        var result = await _host.CallAsync(function, Call.New().Data("A", "\"A\"").Memo("floating", "null"));

        result.StatusCode.ShouldBe(200);
        result.Body.ShouldBe("\"done\"");
    }

    [Fact]
    public async Task A_single_new_step_that_is_not_a_run_step_is_reported()
    {
        var function = TestFunction.Define(async context =>
        {
            await context.Step.SleepAsync("pause", TimeSpan.FromMinutes(5));
            return null;
        });

        var result = await _host.CallAsync(function, Call.New());

        result.OpCodes.ShouldBe(["Sleep"]);
    }

    [Fact]
    public async Task A_run_step_alongside_an_async_step_is_planned_not_executed()
    {
        var function = TestFunction.Define(async context =>
        {
            await Task.WhenAll(
                context.Step.RunAsync("A", () => Record("A")),
                context.Step.SleepAsync("pause", TimeSpan.FromMinutes(5)));
            return null;
        });

        var result = await _host.CallAsync(function, Call.New());

        result.OpCodes.ShouldBe(["StepPlanned", "Sleep"]);
        _ran.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_function_that_changed_falls_back_to_the_earliest_memoized_step_and_warns_once()
    {
        // The run recorded "removed" first, but the code no longer calls it.
        var function = TestFunction.Define(async context =>
        {
            var a = await context.Step.RunAsync("A", () => Record("A"));
            var b = await context.Step.RunAsync("B", () => Record("B"));
            return a + b;
        });

        var result = await _host.CallAsync(
            function,
            Call.New().Data("removed", "\"x\"").Data("A", "\"A\"").Data("B", "\"B\""));

        result.StatusCode.ShouldBe(200);
        result.Body.ShouldBe("\"AB\"");
        _host.Logs.GetSnapshot().Count(record => record.Id.Id == 100).ShouldBe(1);
    }

    [Fact]
    public async Task Results_missing_from_the_stack_are_still_handed_back()
    {
        var function = TestFunction.Define(async context => (object?)await context.Step.RunAsync("A", () => Record("A")));

        var result = await _host.CallAsync(function, Call.New().Data("A", "\"from memo\"").Stack());

        result.Body.ShouldBe("\"from memo\"");
    }

    [Fact]
    public async Task A_step_with_edited_input_is_not_a_result()
    {
        var function = TestFunction.Define(async context => (object?)await context.Step.RunAsync("A", () => Record("A")));

        var result = await _host.CallAsync(function, Call.New().Input("A", "[1]"));

        result.OpCodes.ShouldBe(["StepRun"]);
        _ran.ShouldBe(["A"]);
    }

    [Fact]
    public async Task Waiting_for_an_event_changes_the_path_the_function_takes()
    {
        var function = TestFunction.Define<Payload>(async context =>
        {
            var received = await context.Step.WaitForEventAsync<Payload>("wait-id", new WaitForEventOptions("app/paid", TimeSpan.FromHours(2)));
            return received?.Data.Value switch
            {
                1 => await context.Step.RunAsync("A", () => Record("A")),
                2 => await context.Step.RunAsync("B", () => Record("B")),
                _ => "timed out",
            };
        });

        var waiting = await _host.CallAsync(function, Call.New());
        var pathA = await _host.CallAsync(function, Call.New().Memo("wait-id", """{"data":{"value":1},"id":"e1","name":"app/paid","ts":1,"user":{}}"""));
        var pathB = await _host.CallAsync(function, Call.New().Memo("wait-id", """{"data":{"value":2},"id":"e2","name":"app/paid","ts":1,"user":{}}"""));
        var timedOut = await _host.CallAsync(function, Call.New().Memo("wait-id", "null"));

        waiting.OpCodes.ShouldBe(["WaitForEvent"]);
        pathA.DisplayNames.ShouldBe(["A"]);
        pathB.DisplayNames.ShouldBe(["B"]);
        timedOut.Body.ShouldBe("\"timed out\"");
        _ran.ShouldBe(["A", "B"]);
    }

    private FunctionDefinition AllThenC() => TestFunction.Define(async context =>
    {
        var results = await Task.WhenAll(
            context.Step.RunAsync("A", () => Record("A")),
            context.Step.RunAsync("B", () => Record("B")));
        var c = await context.Step.RunAsync("C", () => Record("C"));
        return results[0] + results[1] + c;
    });

    private FunctionDefinition Race() => TestFunction.Define(async context =>
    {
        var winner = await await Task.WhenAny(
            context.Step.RunAsync("A", () => Record("A")),
            context.Step.RunAsync("B", () => Record("B")));
        return winner switch
        {
            "A" => await context.Step.RunAsync("A wins", () => Record("A wins")),
            "B" => await context.Step.RunAsync("B wins", () => Record("B wins")),
            _ => null,
        };
    });

    // Promise.race([run("A"), run("B").then(() => run("B2"))]) in the TypeScript tests.
    private FunctionDefinition DeepRace() => TestFunction.Define(async context =>
    {
        async Task<string> BThenB2()
        {
            await context.Step.RunAsync("B", () => Record("B"));
            return await context.Step.RunAsync("B2", () => Record("B2"));
        }

        var winner = await await Task.WhenAny(context.Step.RunAsync("A", () => Record("A")), BThenB2());
        return winner switch
        {
            "A" => await context.Step.RunAsync("A wins", () => Record("A wins")),
            "B2" => await context.Step.RunAsync("B wins", () => Record("B wins")),
            _ => null,
        };
    });

    private async Task<string> RunOrFallback(IStepTools step)
    {
        try
        {
            return await step.RunAsync<string>("B", _ => throw new InvalidOperationException("B failed message"));
        }
        catch (StepFailedException)
        {
            return await step.RunAsync("B failed", () => Record("B failed"));
        }
    }

    private string Record(string id)
    {
        _ran.Add(id);
        return id;
    }

    internal sealed record Payload(int Value);
}
