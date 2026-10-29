using Inngest.Functions;
using Inngest.Steps;
using Inngest.Tests.Execution;

namespace Inngest.Tests.Steps;

// Race mode: every op created inside ParallelAsync(ParallelMode.Race, ...) carries opts.parallelMode, so that Inngest
// calls the function again after each step finishes instead of once all have, and such a step is never run inline.
public sealed class ParallelModeTests : IDisposable
{
    private readonly EngineHost _host = new();
    private readonly List<string> _ran = [];

    public void Dispose() => _host.Dispose();

    [Fact]
    public async Task Steps_created_in_a_race_are_planned_with_the_race_mode()
    {
        var function = TestFunction.Define(async context =>
            await await context.Step.ParallelAsync(ParallelMode.Race, () => Task.WhenAny(
                context.Step.RunAsync("a", () => Ran("a")),
                context.Step.RunAsync(new StepOptions("b") { Name = "Second" }, () => Ran("b")))));

        var result = await _host.CallAsync(function, Call.New());

        result.Body.ShouldBe(
            "[" +
            $$$"""{"id":"{{{Call.Hash("a")}}}","op":"StepPlanned","name":"a","displayName":"a","opts":{"type":"step.run","parallelMode":"race"},"userland":{"id":"a"}},""" +
            $$$"""{"id":"{{{Call.Hash("b")}}}","op":"StepPlanned","name":"b","displayName":"Second","opts":{"type":"step.run","parallelMode":"race"},"userland":{"id":"b"}}""" +
            "]");
        _ran.ShouldBeEmpty();
    }

    [Fact]
    public async Task Every_kind_of_step_carries_the_race_mode()
    {
        var function = TestFunction.Define(async context =>
        {
            await context.Step.ParallelAsync(ParallelMode.Race, () => Task.WhenAny(
                context.Step.SleepAsync("cool-off", TimeSpan.FromMinutes(90)),
                context.Step.SleepUntilAsync("until", new DateTimeOffset(2026, 10, 30, 8, 0, 0, TimeSpan.Zero)),
                context.Step.WaitForEventAsync("approved", new WaitForEventOptions("app/approved", TimeSpan.FromDays(1)) { Match = "data.orderId" }),
                context.Step.InvokeAsync<int>("invoke-billing", new InvokeOptions("billing") { Data = new { amount = 5 } }),
                context.Step.SendEventAsync("notify", new InngestEvent("app/shipped", new { orderId = 1 }))));
            return null;
        });

        var result = await _host.CallAsync(function, Call.New());

        result.Body.ShouldBe(
            "[" +
            $$$"""{"id":"{{{Call.Hash("cool-off")}}}","op":"Sleep","name":"1h30m","displayName":"cool-off","opts":{"duration":"1h30m","parallelMode":"race"},"userland":{"id":"cool-off"}},""" +
            $$$"""{"id":"{{{Call.Hash("until")}}}","op":"Sleep","name":"2026-10-30T08:00:00.000Z","displayName":"until","opts":{"duration":"2026-10-30T08:00:00.000Z","parallelMode":"race"},"userland":{"id":"until"}},""" +
            $$$"""{"id":"{{{Call.Hash("approved")}}}","op":"WaitForEvent","name":"app/approved","displayName":"approved","opts":{"event":"app/approved","timeout":"1d","if":"event.data.orderId == async.data.orderId","parallelMode":"race"},"userland":{"id":"approved"}},""" +
            $$$"""{"id":"{{{Call.Hash("invoke-billing")}}}","op":"InvokeFunction","displayName":"invoke-billing","opts":{"function_id":"test-app-billing","payload":{"data":{"amount":5}},"parallelMode":"race"},"userland":{"id":"invoke-billing"}},""" +
            $$$"""{"id":"{{{Call.Hash("notify")}}}","op":"StepPlanned","name":"sendEvent","displayName":"notify","opts":{"type":"step.sendEvent","parallelMode":"race"},"userland":{"id":"notify"}}""" +
            "]");
    }

    [Fact]
    public async Task Steps_outside_any_callback_carry_no_mode()
    {
        var function = TestFunction.Define(async context =>
        {
            await Task.WhenAll(context.Step.RunAsync("a", () => Ran("a")), context.Step.SleepAsync("nap", TimeSpan.FromSeconds(1)));
            return null;
        });

        var result = await _host.CallAsync(function, Call.New());

        ModesOf(result).ShouldBe(["a: -", "nap: -"]);
    }

    // The TypeScript SDK's "does not re-execute a pending step when another parallel step finishes": A then B advance
    // while C runs in a call of its own, so after B the call must plan C again, which Inngest ignores, not run it.
    [Fact]
    public async Task A_lone_new_step_of_a_race_is_planned_instead_of_run()
    {
        var result = await _host.CallAsync(BranchesThenFinal(ParallelMode.Race), Call.New().Data("A", "\"A\"").Data("B", "\"B\""));

        result.OpCodes.ShouldBe(["StepPlanned"]);
        result.DisplayNames.ShouldBe(["C"]);
        result.Op().GetProperty("opts").GetProperty("parallelMode").GetString().ShouldBe("race");
        _ran.ShouldBeEmpty();
    }

    [Fact]
    public async Task Without_a_race_the_lone_new_step_runs_right_away()
    {
        var result = await _host.CallAsync(BranchesThenFinal(ParallelMode.Wait), Call.New().Data("A", "\"A\"").Data("B", "\"B\""));

        result.OpCodes.ShouldBe(["StepRun"]);
        result.DisplayNames.ShouldBe(["C"]);
        _ran.ShouldBe(["C"]);
    }

    [Fact]
    public async Task A_targeted_call_runs_a_step_of_a_race_and_reports_its_mode()
    {
        var result = await _host.CallAsync(BranchesThenFinal(ParallelMode.Race), Call.New().Data("A", "\"A\"").DisableImmediateExecution(), stepId: Call.Hash("C"));

        result.OpCodes.ShouldBe(["StepRun"]);
        result.Op().GetProperty("opts").GetRawText().ShouldBe("""{"type":"step.run","parallelMode":"race"}""");
        _ran.ShouldBe(["C"]);
    }

    [Fact]
    public async Task The_mode_ends_with_the_callback_and_the_next_lone_step_runs_right_away()
    {
        var result = await _host.CallAsync(BranchesThenFinal(ParallelMode.Race), Call.New().Data("A", "\"A\"").Data("B", "\"B\"").Data("C", "\"C\""));

        result.OpCodes.ShouldBe(["StepRun"]);
        result.DisplayNames.ShouldBe(["Final"]);
        result.Op().GetProperty("opts").GetRawText().ShouldBe("""{"type":"step.run"}""");
        _ran.ShouldBe(["Final"]);
    }

    [Fact]
    public async Task The_mode_flows_across_awaits_inside_the_callback()
    {
        var function = TestFunction.Define(async context =>
            await context.Step.ParallelAsync(ParallelMode.Race, async () =>
            {
                await context.Step.RunAsync("first", () => Ran("first"));
                await Task.Yield();
                return await context.Step.RunAsync("second", () => Ran("second"));
            }));

        var result = await _host.CallAsync(function, Call.New().Data("first", "\"first\""));

        result.OpCodes.ShouldBe(["StepPlanned"]);
        result.DisplayNames.ShouldBe(["second"]);
        result.Op().GetProperty("opts").GetProperty("parallelMode").GetString().ShouldBe("race");
    }

    [Fact]
    public async Task The_mode_flows_into_Task_Run_inside_the_callback()
    {
        var function = TestFunction.Define(async context =>
            await context.Step.ParallelAsync(ParallelMode.Race, () => Task.Run(() => context.Step.RunAsync("pooled", () => Ran("pooled")))));

        var result = await _host.CallAsync(function, Call.New());

        result.OpCodes.ShouldBe(["StepPlanned"]);
        result.Op().GetProperty("opts").GetProperty("parallelMode").GetString().ShouldBe("race");
        _ran.ShouldBeEmpty();
    }

    [Fact]
    public async Task Code_after_the_callback_returned_is_outside_it_even_while_the_callback_is_suspended()
    {
        var function = TestFunction.Define(async context =>
        {
            var race = context.Step.ParallelAsync(ParallelMode.Race, async () => await context.Step.RunAsync("inside", () => Ran("inside")));
            var after = context.Step.RunAsync("outside", () => Ran("outside"));
            await Task.WhenAll(race, after);
            return null;
        });

        var result = await _host.CallAsync(function, Call.New());

        ModesOf(result).ShouldBe(["inside: race", "outside: -"]);
    }

    [Fact]
    public async Task An_inner_callback_sets_the_mode_for_its_own_steps_only()
    {
        var function = TestFunction.Define(async context =>
        {
            await context.Step.ParallelAsync(ParallelMode.Race, async () =>
            {
                var waiting = context.Step.ParallelAsync(ParallelMode.Wait, () => Task.WhenAll(
                    context.Step.RunAsync("wait-1", () => Ran("wait-1")),
                    context.Step.RunAsync("wait-2", () => Ran("wait-2"))));
                var racing = context.Step.RunAsync("race-1", () => Ran("race-1"));
                await Task.WhenAll(waiting, racing);
            });
            return null;
        });

        var result = await _host.CallAsync(function, Call.New());

        ModesOf(result).ShouldBe(["wait-1: -", "wait-2: -", "race-1: race"]);
    }

    [Fact]
    public async Task A_step_option_wins_over_the_mode_of_the_callback()
    {
        var function = TestFunction.Define(async context =>
        {
            await context.Step.ParallelAsync(ParallelMode.Race, () => Task.WhenAll(
                context.Step.RunAsync(new StepOptions("opted-out") { ParallelMode = ParallelMode.Wait }, () => Ran("opted-out")),
                context.Step.RunAsync(new StepOptions("named") { Name = "Named" }, () => Ran("named")),
                context.Step.RunAsync("plain", () => Ran("plain"))));
            return null;
        });

        var result = await _host.CallAsync(function, Call.New());

        ModesOf(result).ShouldBe(["opted-out: -", "named: race", "plain: race"]);
    }

    [Fact]
    public async Task A_step_can_join_a_race_on_its_own()
    {
        var function = TestFunction.Define(async context =>
            await context.Step.RunAsync(new StepOptions("alone") { ParallelMode = ParallelMode.Race }, () => Ran("alone")));

        var result = await _host.CallAsync(function, Call.New());

        result.OpCodes.ShouldBe(["StepPlanned"]);
        result.Op().GetProperty("opts").GetRawText().ShouldBe("""{"type":"step.run","parallelMode":"race"}""");
        _ran.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_race_resolves_with_the_step_that_finished_first_on_every_replay()
    {
        var function = TestFunction.Define(async context =>
        {
            var winner = await await context.Step.ParallelAsync(ParallelMode.Race, () => Task.WhenAny(
                context.Step.WaitForEventAsync("approved", new WaitForEventOptions("app/approved", TimeSpan.FromDays(1))),
                context.Step.WaitForEventAsync("rejected", new WaitForEventOptions("app/rejected", TimeSpan.FromDays(1)))));
            return await context.Step.RunAsync("decide", () => Ran(winner!.Name));
        });
        var rejected = """{"name":"app/rejected","data":{},"id":"01JREJ","ts":1793268000000}""";

        var result = await _host.CallAsync(function, Call.New().Memo("rejected", rejected));

        // The wait that lost is still open and is reported again, which Inngest ignores; the step after the race goes
        // ahead in the same response instead of waiting for it.
        result.OpCodes.ShouldBe(["WaitForEvent", "StepPlanned"]);
        ModesOf(result).ShouldBe(["approved: race", "decide: -"]);
        _ran.ShouldBeEmpty();

        var replay = await _host.CallAsync(function, Call.New().Memo("rejected", rejected).Data("decide", "\"app/rejected\"").Memo("approved", "null"));
        replay.StatusCode.ShouldBe(200);
        replay.Body.ShouldBe("\"app/rejected\"");
    }

    [Fact]
    public async Task The_callback_can_return_a_plain_task()
    {
        var function = TestFunction.Define(async context =>
        {
            await context.Step.ParallelAsync(ParallelMode.Race, () => Task.WhenAll(
                (Task)context.Step.RunAsync("a", () => Ran("a")),
                context.Step.SleepAsync("nap", TimeSpan.FromSeconds(1))));
            return null;
        });

        var result = await _host.CallAsync(function, Call.New());

        ModesOf(result).ShouldBe(["a: race", "nap: race"]);
    }

    [Fact]
    public async Task The_scope_is_async_local_and_restored_after_the_callback()
    {
        ParallelScope.Mode.ShouldBe(ParallelMode.Wait);
        var seen = new List<ParallelMode>();

        var result = await ParallelScope.Run(ParallelMode.Race, async () =>
        {
            seen.Add(ParallelScope.Mode);
            await Task.Yield();
            seen.Add(ParallelScope.Mode);
            seen.Add(await Task.Run(() => ParallelScope.Mode, TestContext.Current.CancellationToken));
            seen.Add(await ParallelScope.Run(ParallelMode.Wait, () => Task.FromResult(ParallelScope.Mode)));
            seen.Add(ParallelScope.Mode);
            return 42;
        });

        result.ShouldBe(42);
        seen.ShouldBe([ParallelMode.Race, ParallelMode.Race, ParallelMode.Race, ParallelMode.Wait, ParallelMode.Race]);
        ParallelScope.Mode.ShouldBe(ParallelMode.Wait);
    }

    [Fact]
    public async Task A_callback_that_throws_gives_a_faulted_task_and_restores_the_scope()
    {
        var task = ParallelScope.Run<int>(ParallelMode.Race, () => throw new InvalidOperationException("boom"));

        task.IsFaulted.ShouldBeTrue();
        ParallelScope.Mode.ShouldBe(ParallelMode.Wait);
        (await Should.ThrowAsync<InvalidOperationException>(task)).Message.ShouldBe("boom");
    }

    private FunctionDefinition BranchesThenFinal(ParallelMode mode) =>
        TestFunction.Define(async context =>
        {
            async Task<string> Branch()
            {
                await context.Step.RunAsync("A", () => Ran("A"));
                return await context.Step.RunAsync("B", () => Ran("B"));
            }

            await context.Step.ParallelAsync(mode, () => Task.WhenAll(Branch(), context.Step.RunAsync("C", () => Ran("C"))));
            return await context.Step.RunAsync("Final", () => Ran("Final"));
        });

    private static string[] ModesOf(CallResult result) =>
        [.. result.Ops.Select(op => $"{op.GetProperty("userland").GetProperty("id").GetString()}: " +
            (op.GetProperty("opts").TryGetProperty("parallelMode", out var mode) ? mode.GetString() : "-"))];

    private string Ran(string name)
    {
        _ran.Add(name);
        return name;
    }
}
