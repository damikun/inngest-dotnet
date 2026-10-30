using Inngest.Execution;
using Inngest.Testing;
using static Inngest.Tests.Testing.TestHosts;

namespace Inngest.Tests.Testing;

// How the host answers ops the engine does not produce yet, from a scripted engine: steps it does not know, race
// mode, and a planned step that a targeted call does not find.
public sealed class TestHostProtocolTests
{
    [Theory]
    [InlineData("WaitForSignal", """{"signal":"order-1","timeout":"1h"}""", StepType.WaitForSignal)]
    [InlineData("Gateway", """{"url":"https://example.com","method":"GET"}""", StepType.Fetch)]
    [InlineData("AIGateway", """{"type":"step.ai.infer"}""", StepType.AIInfer)]
    public async Task A_step_without_a_mock_leaves_the_run_waiting_on_it(string op, string opts, StepType type)
    {
        var engine = new ScriptedEngine((206, $"[{ScriptedEngine.Op("approval", op, opts)}]"));
        await using var host = Host(engine);

        var run = await host.RunAsync("fn", Event(), Cancel);

        run.Status.ShouldBe(TestRunStatus.Waiting);
        var step = run.WaitingOn.ShouldHaveSingleItem();
        step.Type.ShouldBe(type);
        step.Status.ShouldBe(TestStepStatus.Pending);
        run.ToString().ShouldBe($"Waiting on step \"approval\" ({op}).");
    }

    [Theory]
    [InlineData("DeferAdd", "{}", null)]
    public async Task A_step_the_host_does_not_carry_out_leaves_the_run_waiting_on_it(string op, string opts, StepType? type)
    {
        var engine = new ScriptedEngine((206, $"[{ScriptedEngine.Op("approval", op, opts)}]"));
        await using var host = Host(engine);

        var run = await host.RunAsync("fn", Event(), Cancel);

        run.Status.ShouldBe(TestRunStatus.Waiting);
        var step = run.WaitingOn.ShouldHaveSingleItem();
        step.Id.ShouldBe("approval");
        step.Type.ShouldBe(type);
        step.Op.ShouldBe(op);
        step.Status.ShouldBe(TestStepStatus.Pending);
        run.ToString().ShouldBe($"Waiting on step \"approval\" ({op}, which the test host does not support).");
    }

    [Fact]
    public async Task Planned_steps_beside_an_unknown_one_still_run()
    {
        var engine = new ScriptedEngine(
            (206, $"[{Planned("a")},{ScriptedEngine.Op("approval", "WaitForSignal", """{"signal":"order-1"}""")}]"),
            (206, $"[{Ran("a", "\"A\"")}]"));
        await using var host = Host(engine);

        var run = await host.RunAsync("fn", Event(), Cancel);

        run.Status.ShouldBe(TestRunStatus.Waiting);
        run.GetStep("a").GetOutput<string>().ShouldBe("A");
        run.WaitingOn.ShouldHaveSingleItem().Id.ShouldBe("approval");
        engine.Targets.ShouldBe(["step", StepIds.Hash("a")]);
        engine.Bodies[1].GetProperty("ctx").GetProperty("disable_immediate_execution").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task A_step_the_host_does_not_know_cannot_be_mocked()
    {
        var engine = new ScriptedEngine((206, $"[{ScriptedEngine.Op("approval", "DeferAdd", "{}")}]"));
        await using var host = Host(engine);

        var exception = await Should.ThrowAsync<InvalidOperationException>(() =>
            host.RunAsync("fn", Event(), new StepMocks().Sleep("approval"), Cancel));

        exception.Message.ShouldBe("Step \"approval\" is a DeferAdd step, which the test host does not support, so it cannot be mocked.");
    }

    [Fact]
    public async Task In_race_mode_the_run_resumes_after_the_first_step_completes()
    {
        var engine = new ScriptedEngine(
            (206, $"[{Wait("approved", race: true)},{Wait("rejected", race: true)}]"),
            (200, "\"approved\""));
        await using var host = Host(engine);

        var run = await host.RunAsync("fn", Event(), new StepMocks().WaitForEvent("approved", new InngestEvent("order/approved")), Cancel);

        run.GetOutput<string>().ShouldBe("approved");
        var resumed = engine.Bodies[1];
        resumed.GetProperty("ctx").GetProperty("stack").GetProperty("stack").EnumerateArray().Select(id => id.GetString())
            .ShouldBe([StepIds.Hash("approved")]);
        resumed.GetProperty("ctx").GetProperty("disable_immediate_execution").GetBoolean().ShouldBeFalse();
        resumed.GetProperty("steps").GetProperty(StepIds.Hash("approved")).GetProperty("name").GetString().ShouldBe("order/approved");
    }

    [Fact]
    public async Task Without_race_mode_the_run_waits_for_every_step()
    {
        var engine = new ScriptedEngine((206, $"[{Wait("approved", race: false)},{Wait("rejected", race: false)}]"));
        await using var host = Host(engine);

        var run = await host.RunAsync("fn", Event(), new StepMocks().WaitForEvent("approved", new InngestEvent("order/approved")), Cancel);

        run.Status.ShouldBe(TestRunStatus.Waiting);
        run.WaitingOn.ShouldHaveSingleItem().Id.ShouldBe("rejected");
        engine.Targets.Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_step_a_race_left_pending_is_not_taken_on_twice()
    {
        // The event arrives at once and resumes the run while the sleep still has an hour to go, so the function
        // reports the sleep again.
        var engine = new ScriptedEngine(
            (206, $"[{Wait("approved", race: true)},{Sleep("nap", race: true)}]"),
            (206, $"[{Sleep("nap", race: true)}]"),
            (200, "\"done\""));
        await using var host = Host(engine);
        var mocks = new StepMocks().WaitForEvent("approved", new InngestEvent("order/approved")).Sleep("nap");

        var run = await host.RunAsync("fn", Event(), mocks, Cancel);

        run.GetOutput<string>().ShouldBe("done");
        run.Steps.Select(step => step.Id).ShouldBe(["approved", "nap"]);
        engine.Bodies[2].GetProperty("ctx").GetProperty("stack").GetProperty("stack").EnumerateArray().Select(id => id.GetString())
            .ShouldBe([StepIds.Hash("approved"), StepIds.Hash("nap")]);
    }

    [Fact]
    public async Task A_planned_step_that_a_targeted_call_does_not_find_fails_the_run_without_retries()
    {
        var notFound = $$"""{"error":"Could not find step \"{{StepIds.Hash("a")}}\" to run; timed out.","requestedStep":"{{StepIds.Hash("a")}}","foundSteps":[],"totalFoundSteps":0}""";
        var engine = new ScriptedEngine((206, $"[{Planned("a")},{Planned("b")}]"), (500, notFound));
        await using var host = Host(engine);

        var run = await host.RunAsync("fn", Event(), Cancel);

        run.Status.ShouldBe(TestRunStatus.Failed);
        run.Error.ShouldBeOfType<InvalidOperationException>().Message.ShouldStartWith("Step \"a\" was planned, but the function did not reach it");
        engine.Targets.Count.ShouldBe(2);
    }

    private static InngestTestHost Host(ScriptedEngine engine) =>
        new(Services(_ => Task.FromResult<object?>(null)), _ => engine);

    private static string Planned(string id) => ScriptedEngine.Op(id, "StepPlanned", """{"type":"step.run"}""");

    private static string Ran(string id, string data) =>
        $$"""{"id":"{{StepIds.Hash(id)}}","op":"StepRun","name":"{{id}}","displayName":"{{id}}","opts":{"type":"step.run"},"userland":{"id":"{{id}}"},"data":{{data}}}""";

    private static string Sleep(string id, bool race) =>
        ScriptedEngine.Op(id, "Sleep", $$"""{"duration":"1h"{{(race ? ",\"parallelMode\":\"race\"" : string.Empty)}}}""");

    private static string Wait(string id, bool race) =>
        ScriptedEngine.Op(id, "WaitForEvent", $$"""{"event":"order/{{id}}","timeout":"1d"{{(race ? ",\"parallelMode\":\"race\"" : string.Empty)}}}""");
}
