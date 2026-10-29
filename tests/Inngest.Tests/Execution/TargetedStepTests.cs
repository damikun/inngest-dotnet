using System.Text.Json;
using Inngest.Functions;

namespace Inngest.Tests.Execution;

public sealed class TargetedStepTests : IDisposable
{
    private readonly EngineHost _host = new();
    private readonly List<string> _ran = [];

    public void Dispose() => _host.Dispose();

    [Fact]
    public async Task The_requested_step_runs_even_when_other_new_steps_were_found_first()
    {
        var function = ThreeInParallel();

        var result = await _host.CallAsync(function, Call.New().DisableImmediateExecution(), stepId: Call.Hash("C"));

        result.OpCodes.ShouldBe(["StepRun"]);
        result.DisplayNames.ShouldBe(["C"]);
        _ran.ShouldBe(["C"]);
    }

    [Fact]
    public async Task Memoized_steps_replay_before_the_requested_step_is_reached()
    {
        var function = TestFunction.Define(async context =>
        {
            var a = await context.Step.RunAsync("A", () => Record("A"));
            await Task.WhenAll(context.Step.RunAsync("B", () => Record(a + "B")), context.Step.RunAsync("C", () => Record(a + "C")));
            return null;
        });

        var result = await _host.CallAsync(function, Call.New().Data("A", "\"memo\"").DisableImmediateExecution(), stepId: Call.Hash("C"));

        result.Op().GetProperty("data").GetString().ShouldBe("memoC");
        _ran.ShouldBe(["memoC"]);
    }

    [Fact]
    public async Task A_targeted_call_whose_function_returns_answers_with_the_output()
    {
        var function = TestFunction.Define(async context =>
        {
            await context.Step.RunAsync("A", () => Record("A"));
            return "done";
        });

        var result = await _host.CallAsync(function, Call.New().Data("A", "\"A\""), stepId: Call.Hash("missing-step"));

        result.StatusCode.ShouldBe(200);
        result.Body.ShouldBe("\"done\"");
    }

    [Fact]
    public async Task A_run_step_the_function_never_awaits_still_runs()
    {
        var function = TestFunction.Define(async context =>
        {
            await context.Step.RunAsync("A", () => Record("A"));
            _ = context.Step.RunAsync("background", () => Record("background"));
            return "done";
        });

        var discovery = await _host.CallAsync(function, Call.New().Data("A", "\"A\""));
        var targeted = await _host.CallAsync(function, Call.New().Data("A", "\"A\""), stepId: Call.Hash("background"));
        var final = await _host.CallAsync(function, Call.New().Data("A", "\"A\"").Data("background", "\"background\""));

        discovery.OpCodes.ShouldBe(["StepPlanned"]);
        discovery.DisplayNames.ShouldBe(["background"]);
        targeted.OpCodes.ShouldBe(["StepRun"]);
        targeted.Op().GetProperty("data").GetString().ShouldBe("background");
        final.StatusCode.ShouldBe(200);
        final.Body.ShouldBe("\"done\"");
        _ran.ShouldBe(["background"]);
    }

    [Fact]
    public async Task The_requested_step_can_be_reached_after_the_function_waits_on_its_own_work()
    {
        var function = TestFunction.Define(async context =>
        {
            await Task.Delay(TimeSpan.FromSeconds(5), _host.Time);
            await context.Step.RunAsync("B", () => Record("B"));
            return null;
        });

        var pending = _host.Start(function, Call.New(), stepId: Call.Hash("B"));
        pending.IsCompleted.ShouldBeFalse();
        _host.Time.Advance(TimeSpan.FromSeconds(5));
        using var result = await pending;

        result.StatusCode.ShouldBe(206);
        _ran.ShouldBe(["B"]);
    }

    [Fact]
    public async Task A_step_that_does_not_show_up_within_ten_seconds_fails_the_call()
    {
        var function = ThreeInParallel();
        var missing = Call.Hash("missing-step");

        var pending = _host.Start(function, Call.New().Data("A", "\"A\"").DisableImmediateExecution(), stepId: missing);
        _host.Time.Advance(TimeSpan.FromSeconds(9));
        pending.IsCompleted.ShouldBeFalse();
        _host.Time.Advance(TimeSpan.FromSeconds(1));
        using var result = await pending;

        result.StatusCode.ShouldBe(500);
        result.NoRetry.ShouldBe(false);

        // Sorted by hashed id, as the TypeScript SDK reports them.
        var b = Call.Hash("B");
        var c = Call.Hash("C");
        var body = JsonDocument.Parse(result.Body.WrittenMemory).RootElement;
        body.GetProperty("error").GetString().ShouldBe(
            $"Could not find step \"{missing}\" to run; timed out. Found new steps: \nC ({c})\nB ({b}).");
        body.GetProperty("requestedStep").GetString().ShouldBe(missing);
        body.GetProperty("foundSteps").GetRawText().ShouldBe(
            $$$"""[{"id":"{{{c}}}","name":"C","displayName":"C"},{"id":"{{{b}}}","name":"B","displayName":"B"}]""");
        body.GetProperty("totalFoundSteps").GetInt32().ShouldBe(2);
        _ran.ShouldBeEmpty();
        _host.Logs.GetSnapshot().ShouldContain(record => record.Id.Id == 102);
    }

    [Fact]
    public async Task The_not_found_report_lists_at_most_25_steps_sorted_by_id()
    {
        var function = TestFunction.Define(async context =>
        {
            await Task.WhenAll(Enumerable.Range(0, 30).Select(i => context.Step.RunAsync($"step-{i}", () => i)));
            return null;
        });

        var pending = _host.Start(function, Call.New().Data("step-0", "0"), stepId: Call.Hash("missing-step"));
        _host.Time.Advance(TimeSpan.FromSeconds(10));
        using var result = await pending;

        var body = JsonDocument.Parse(result.Body.WrittenMemory).RootElement;
        body.GetProperty("totalFoundSteps").GetInt32().ShouldBe(29);
        var ids = body.GetProperty("foundSteps").EnumerateArray().Select(step => step.GetProperty("id").GetString()!).ToList();
        ids.Count.ShouldBe(25);
        ids.ShouldBe([.. ids.Order(StringComparer.Ordinal)]);
        ids.ShouldNotContain(Call.Hash("step-0"));
        body.GetProperty("error").GetString()!.ShouldEndWith(" (showing 25 of 29)");
    }

    [Fact]
    public async Task The_wait_restarts_whenever_the_function_finds_other_steps()
    {
        var function = TestFunction.Define(async context =>
        {
            _ = context.Step.RunAsync("A", () => Record("A"));
            await Task.Delay(TimeSpan.FromSeconds(8), _host.Time);
            _ = context.Step.RunAsync("B", () => Record("B"));
            await Task.Delay(TimeSpan.FromSeconds(8), _host.Time);
            await context.Step.RunAsync("C", () => Record("C"));
            return null;
        });

        // Timers in order: the function's first delay, the engine's deadline, the second delay, the new deadline.
        var pending = _host.Start(function, Call.New().DisableImmediateExecution(), stepId: Call.Hash("C"));
        await _host.Time.TimersCreated(2);
        _host.Time.Advance(TimeSpan.FromSeconds(8));
        await _host.Time.TimersCreated(4);
        _host.Time.Advance(TimeSpan.FromSeconds(8));
        using var result = await pending;

        result.StatusCode.ShouldBe(206);
        _ran.ShouldBe(["C"]);
    }

    private FunctionDefinition ThreeInParallel() => TestFunction.Define(async context =>
    {
        await Task.WhenAll(
            context.Step.RunAsync("A", () => Record("A")),
            context.Step.RunAsync("B", () => Record("B")),
            context.Step.RunAsync("C", () => Record("C")));
        return null;
    });

    private string Record(string id)
    {
        lock (_ran)
        {
            _ran.Add(id);
        }

        return id;
    }
}
