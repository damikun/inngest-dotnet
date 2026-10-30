using System.Globalization;
using System.Text.Json;
using Inngest.Functions;
using Inngest.Tests.Execution;

namespace Inngest.Tests.FanOut;

// The fan-out on the real engine: the steps it plans, what a replay hands back in whichever order the results were
// recorded, and what happens when an item fails.
public sealed class FanOutEngineTests : IDisposable
{
    private static readonly string[] Letters = ["a", "b", "c"];
    private static readonly string[] Six = ["a", "b", "c", "d", "e", "f"];
    private static readonly string[] Nine = [.. Six, "g", "h", "i"];

    private readonly EngineHost _host = new();

    public void Dispose() => _host.Dispose();

    [Fact]
    public async Task Each_item_is_a_step_with_the_id_of_its_position()
    {
        var result = await _host.CallAsync(Upper(Letters), Call.New());

        result.StatusCode.ShouldBe(206);
        result.OpCodes.ShouldBe(["StepPlanned", "StepPlanned", "StepPlanned"]);
        result.DisplayNames.ShouldBe(["charge[0]", "charge[1]", "charge[2]"]);
        Ids(result).ShouldBe(Hashed("charge[0]", "charge[1]", "charge[2]"));
        result.Ops.Select(op => op.GetProperty("userland").GetProperty("id").GetString()).ShouldBe(["charge[0]", "charge[1]", "charge[2]"]);
    }

    [Fact]
    public async Task The_ids_of_the_items_cannot_be_the_numbered_ids_of_a_repeated_id()
    {
        // The engine numbers the repeats of an id "charge", "charge:1", "charge:2". The ids of the items end in a bracket,
        // which a number never does.
        var function = TestFunction.Define(async context =>
        {
            var first = context.Step.RunAsync("charge", () => 1);
            var second = context.Step.RunAsync("charge", () => 2);
            var third = context.Step.RunAsync("charge", () => 3);
            var items = context.Step.MapAsync("charge", Letters, (item, _) => Task.FromResult(item));
            await Task.WhenAll(first, second, third, items);
            return null;
        });

        var result = await _host.CallAsync(function, Call.New());

        var ids = Ids(result);
        ids.ShouldBe(Hashed("charge", "charge:1", "charge:2", "charge[0]", "charge[1]", "charge[2]"));
        ids.Distinct().Count().ShouldBe(6);
    }

    [Fact]
    public async Task A_step_of_the_codes_own_that_spells_an_item_id_is_numbered_like_any_repeated_id()
    {
        var function = TestFunction.Define(async context =>
        {
            var own = context.Step.RunAsync("charge[0]", () => 1);
            var items = context.Step.MapAsync("charge", Letters, (item, _) => Task.FromResult(item));
            await Task.WhenAll(own, items);
            return null;
        });

        var result = await _host.CallAsync(function, Call.New());

        Ids(result).ShouldBe(Hashed("charge[0]", "charge[0]:1", "charge[1]", "charge[2]"));
    }

    [Fact]
    public async Task Two_fan_outs_with_the_same_id_get_the_numbered_ids_of_a_repeated_id()
    {
        var function = TestFunction.Define(async context =>
        {
            var first = context.Step.MapAsync("charge", Letters[..2], (item, _) => Task.FromResult(item));
            var second = context.Step.MapAsync("charge", Letters[..2], (item, _) => Task.FromResult(item));
            await Task.WhenAll(first, second);
            return null;
        });

        var result = await _host.CallAsync(function, Call.New());

        Ids(result).ShouldBe(Hashed("charge[0]", "charge[1]", "charge[0]:1", "charge[1]:1"));
    }

    [Fact]
    public async Task Results_come_back_in_the_order_of_the_items_whatever_order_the_steps_finished_in()
    {
        var result = await _host.CallAsync(
            Upper(Letters),
            Call.New().Data("charge[2]", "\"C\"").Data("charge[0]", "\"A\"").Data("charge[1]", "\"B\""));

        result.StatusCode.ShouldBe(200);
        result.Body.ShouldBe("""["A","B","C"]""");
    }

    [Fact]
    public async Task A_list_without_items_runs_no_step()
    {
        var result = await _host.CallAsync(Upper([]), Call.New());

        result.StatusCode.ShouldBe(200);
        result.Body.ShouldBe("[]");
    }

    [Fact]
    public async Task A_last_item_on_its_own_runs_in_the_call_that_finds_it()
    {
        var ran = new List<string>();

        var result = await _host.CallAsync(Upper(Letters, ran: ran), Call.New().Data("charge[0]", "\"A\"").Data("charge[1]", "\"B\""));

        result.OpCodes.ShouldBe(["StepRun"]);
        result.DisplayNames.ShouldBe(["charge[2]"]);
        ran.ShouldBe(["c"]);
    }

    [Fact]
    public async Task Only_the_first_window_is_planned_at_first()
    {
        var result = await _host.CallAsync(Upper(Six, new FanOutOptions { MaxParallelism = 2 }), Call.New());

        result.OpCodes.ShouldBe(["StepPlanned", "StepPlanned"]);
        result.DisplayNames.ShouldBe(["charge[0]", "charge[1]"]);
    }

    [Fact]
    public async Task The_window_moves_on_once_its_results_are_recorded()
    {
        var function = Upper(Six, new FanOutOptions { MaxParallelism = 2 });

        var second = await _host.CallAsync(function, Call.New().Data("charge[0]", "\"A\"").Data("charge[1]", "\"B\"").DisableImmediateExecution());
        var third = await _host.CallAsync(
            function,
            Call.New().Data("charge[0]", "\"A\"").Data("charge[1]", "\"B\"").Data("charge[2]", "\"C\"").Data("charge[3]", "\"D\"").DisableImmediateExecution());

        second.DisplayNames.ShouldBe(["charge[2]", "charge[3]"]);
        third.DisplayNames.ShouldBe(["charge[4]", "charge[5]"]);
    }

    [Theory]
    [InlineData("charge[0]", "charge[1]")]
    [InlineData("charge[1]", "charge[0]")]
    public async Task The_next_window_is_the_same_whichever_item_of_the_window_finished_first(string firstDone, string secondDone)
    {
        var function = Upper(Six, new FanOutOptions { MaxParallelism = 2 });

        var result = await _host.CallAsync(function, Call.New().Data(firstDone, "\"x\"").Data(secondDone, "\"y\"").DisableImmediateExecution());

        result.DisplayNames.ShouldBe(["charge[2]", "charge[3]"]);
    }

    [Fact]
    public async Task A_replay_starts_the_items_of_each_window_as_the_results_are_handed_back_in_the_order_they_were_recorded()
    {
        // Two windows of three finished out of order, 2 0 1 and then 5 3 4. Each result frees a lane to start the next
        // item, so the third window is found while the second window's results are handed back.
        var function = Upper(Nine, new FanOutOptions { MaxParallelism = 3 });
        var recorded = Call.New()
            .Data("charge[2]", "\"C\"").Data("charge[0]", "\"A\"").Data("charge[1]", "\"B\"")
            .Data("charge[5]", "\"F\"").Data("charge[3]", "\"D\"").Data("charge[4]", "\"E\"")
            .DisableImmediateExecution();

        var result = await _host.CallAsync(function, recorded);

        result.DisplayNames.ShouldBe(["charge[6]", "charge[7]", "charge[8]"]);
    }

    [Fact]
    public async Task A_completed_run_gives_the_same_results_for_every_order_the_results_were_recorded_in()
    {
        var items = Six[..4];
        var function = Upper(items, new FanOutOptions { MaxParallelism = 2 });
        string[][] orders =
        [
            ["charge[0]", "charge[1]", "charge[2]", "charge[3]"],
            ["charge[1]", "charge[0]", "charge[3]", "charge[2]"],
            ["charge[1]", "charge[0]", "charge[2]", "charge[3]"],
        ];

        foreach (var order in orders)
        {
            var call = Call.New().DisableImmediateExecution();
            foreach (var id in order)
            {
                var index = int.Parse(id[7..^1], CultureInfo.InvariantCulture);
                call.Data(id, $"\"{items[index].ToUpperInvariant()}\"");
            }

            var result = await _host.CallAsync(function, call);

            result.StatusCode.ShouldBe(200, string.Join(",", order));
            result.Body.ShouldBe("""["A","B","C","D"]""");
        }
    }

    [Fact]
    public async Task In_race_mode_a_result_moves_the_window_before_the_others_have_finished()
    {
        var function = Upper(Six, new FanOutOptions { MaxParallelism = 2, ParallelMode = ParallelMode.Race });

        var first = await _host.CallAsync(function, Call.New());
        var second = await _host.CallAsync(function, Call.New().Data("charge[1]", "\"B\"").DisableImmediateExecution());
        var third = await _host.CallAsync(function, Call.New().Data("charge[1]", "\"B\"").Data("charge[2]", "\"C\"").DisableImmediateExecution());

        // The step without a result is planned again, as the engine does for every pending step of a group.
        first.DisplayNames.ShouldBe(["charge[0]", "charge[1]"]);
        second.DisplayNames.ShouldBe(["charge[0]", "charge[2]"]);
        third.DisplayNames.ShouldBe(["charge[0]", "charge[3]"]);
        first.Ops.Concat(second.Ops).Concat(third.Ops)
            .ShouldAllBe(op => op.GetProperty("opts").GetProperty("parallelMode").GetString() == "race");
    }

    [Fact]
    public async Task Without_race_mode_the_steps_say_nothing_about_a_mode()
    {
        var result = await _host.CallAsync(Upper(Letters), Call.New());

        result.Ops.Any(HasParallelMode).ShouldBeFalse();
    }

    [Fact]
    public async Task A_failure_that_is_seen_before_the_next_item_starts_stops_the_list_there()
    {
        var function = CatchingFailure(Six, new FanOutOptions { MaxParallelism = 2 });

        var result = await _host.CallAsync(
            function,
            Call.New().Failed("charge[1]", "Error", "declined").Data("charge[0]", "\"a\"").DisableImmediateExecution());

        // The exception is the step's own. No step is planned for the items after the window.
        result.StatusCode.ShouldBe(200);
        result.Json.GetProperty("stepId").GetString().ShouldBe("charge[1]");
        result.Json.GetProperty("message").GetString().ShouldBe("declined");
    }

    [Fact]
    public async Task An_item_that_started_before_the_failure_was_seen_is_planned_when_the_function_goes_on()
    {
        var function = CatchingFailure(Six, new FanOutOptions { MaxParallelism = 2 });

        // The result of item 0 is handed back first and starts item 2. A step the function started is reported even
        // if the function no longer waits for it.
        var result = await _host.CallAsync(
            function,
            Call.New().Data("charge[0]", "\"a\"").Failed("charge[1]", "Error", "declined").DisableImmediateExecution());

        result.StatusCode.ShouldBe(206);
        result.DisplayNames.ShouldBe(["charge[2]"]);
    }

    [Fact]
    public async Task An_uncaught_failure_fails_the_run_without_retries_and_without_waiting_for_the_items_in_flight()
    {
        // Item 2 has no result yet, and the run fails anyway.
        var result = await _host.CallAsync(
            Upper(Letters),
            Call.New().Data("charge[0]", "\"A\"").Failed("charge[1]", "Error", "declined").DisableImmediateExecution());

        result.StatusCode.ShouldBe(400);
        result.NoRetry.ShouldBe(true);
        result.Json.GetProperty("message").GetString().ShouldBe("declined");
    }

    [Fact]
    public async Task The_failure_that_was_recorded_first_is_the_one_thrown()
    {
        var result = await _host.CallAsync(
            CatchingFailure(Letters, null),
            Call.New().Failed("charge[2]", "Error", "third item").Failed("charge[0]", "Error", "first item").Data("charge[1]", "\"b\""));

        result.Json.GetProperty("stepId").GetString().ShouldBe("charge[2]");
    }

    [Fact]
    public async Task The_outcomes_of_every_item_come_back_in_order_with_the_errors_of_the_failed_ones()
    {
        var function = TestFunction.Define(async context =>
            (object?)await context.Step.MapOutcomesAsync("charge", Letters, (item, _) => Task.FromResult(item.ToUpperInvariant())));

        var result = await _host.CallAsync(
            function,
            Call.New().Data("charge[2]", "\"C\"").Failed("charge[1]", "PaymentError", "declined").Data("charge[0]", "\"A\""));

        result.StatusCode.ShouldBe(200);
        var outcomes = result.Json.EnumerateArray().ToArray();
        outcomes.Select(outcome => outcome.GetProperty("index").GetInt32()).ShouldBe([0, 1, 2]);
        outcomes[0].GetProperty("value").GetString().ShouldBe("A");
        outcomes[0].TryGetProperty("error", out _).ShouldBeFalse();
        outcomes[1].TryGetProperty("value", out _).ShouldBeFalse();
        outcomes[1].GetProperty("error").GetProperty("name").GetString().ShouldBe("PaymentError");
        outcomes[1].GetProperty("error").GetProperty("message").GetString().ShouldBe("declined");
        outcomes[2].GetProperty("value").GetString().ShouldBe("C");
    }

    [Fact]
    public async Task Collecting_outcomes_goes_on_with_the_next_window_after_a_failure()
    {
        var function = TestFunction.Define(async context =>
            (object?)await context.Step.MapOutcomesAsync("charge", Six, (item, _) => Task.FromResult(item), new FanOutOptions { MaxParallelism = 2 }));

        var result = await _host.CallAsync(function, Call.New().Failed("charge[0]", "Error", "declined").Data("charge[1]", "\"b\"").DisableImmediateExecution());

        result.DisplayNames.ShouldBe(["charge[2]", "charge[3]"]);
    }

    [Fact]
    public async Task ForEachAsync_saves_nothing_for_an_item_and_does_not_run_an_item_that_has_its_result()
    {
        var seen = new List<string>();
        var function = TestFunction.Define(async context =>
        {
            await context.Step.ForEachAsync("notify", Letters, (item, _) =>
            {
                seen.Add(item);
                return Task.CompletedTask;
            });
            return "done";
        });

        var result = await _host.CallAsync(function, Call.New().Memo("notify[0]", "null").Memo("notify[1]", "null").Memo("notify[2]", "null"));

        result.Body.ShouldBe("\"done\"");
        seen.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_token_canceled_while_items_are_left_starts_no_more_of_them()
    {
        var function = TestFunction.Define(async context =>
        {
            using var cancellation = new CancellationTokenSource();

            // Canceled by the first result that is handed back, which is before the result of item 0.
            var gate = context.Step.RunAsync("gate", () => 1);
            _ = gate.ContinueWith(_ => cancellation.Cancel(), TaskContinuationOptions.ExecuteSynchronously);
            return (object?)await context.Step.MapAsync(
                "charge",
                Six,
                (item, _) => Task.FromResult(item),
                new FanOutOptions { MaxParallelism = 1 },
                cancellation.Token);
        });

        var result = await _host.CallAsync(function, Call.New().Data("gate", "1").Data("charge[0]", "\"a\"").DisableImmediateExecution());

        // The function failed with the cancellation, and no step for item 1 was planned.
        result.StatusCode.ShouldBe(500);
        result.NoRetry.ShouldBe(false);
        result.Json.GetProperty("name").GetString()!.ShouldContain("OperationCanceledException");
    }

    private static bool HasParallelMode(JsonElement op) => op.GetProperty("opts").TryGetProperty("parallelMode", out _);

    private static string[] Ids(CallResult result) => [.. result.Ops.Select(op => op.GetProperty("id").GetString()!)];

    private static string[] Hashed(params string[] ids) => [.. ids.Select(Call.Hash)];

    private static FunctionDefinition CatchingFailure(IReadOnlyList<string> items, FanOutOptions? options) =>
        TestFunction.Define(async context =>
        {
            try
            {
                await context.Step.MapAsync("charge", items, (item, _) => Task.FromResult(item), options);
                return "completed";
            }
            catch (StepFailedException exception)
            {
                return new { exception.StepId, exception.Error.Message };
            }
        });

    private static FunctionDefinition Upper(IReadOnlyList<string> items, FanOutOptions? options = null, List<string>? ran = null) =>
        TestFunction.Define(async context => (object?)await context.Step.MapAsync(
            "charge",
            items,
            (item, _) =>
            {
                ran?.Add(item);
                return Task.FromResult(item.ToUpperInvariant());
            },
            options));
}
