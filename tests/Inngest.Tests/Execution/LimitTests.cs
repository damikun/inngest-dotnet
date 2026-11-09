using Microsoft.Extensions.Logging;

namespace Inngest.Tests.Execution;

// Inngest's limits on a step's output, a run's state and the steps of a run, checked before Inngest refuses them.
public sealed class LimitTests : IDisposable
{
    private const int FourMebibytes = 4 * 1024 * 1024;

    private readonly EngineHost _host = new();

    public void Dispose() => _host.Dispose();

    [Fact]
    public async Task A_step_output_over_4_MiB_fails_the_step_without_retries()
    {
        var function = TestFunction.Define(async context => await context.Step.RunAsync("export", () => new string('x', FourMebibytes)));

        var result = await _host.CallAsync(function, Call.New());

        result.OpCodes.ShouldBe(["StepFailed"]);
        result.NoRetry.ShouldBe(true);
        var error = result.Op().GetProperty("error");
        error.GetProperty("name").GetString().ShouldBe(nameof(NonRetriableException));
        error.GetProperty("message").GetString().ShouldBe(
            "The output of step \"export\" is 4.0 MiB, more than the 4 MiB Inngest keeps for a step. Keep large data outside the run and return a reference to it.");
        result.Op().TryGetProperty("data", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task A_step_output_of_4_MiB_is_kept()
    {
        // The quotes of the JSON string make up the rest.
        var function = TestFunction.Define(async context => await context.Step.RunAsync("export", () => new string('x', FourMebibytes - 2)));

        var result = await _host.CallAsync(function, Call.New());

        result.OpCodes.ShouldBe(["StepRun"]);
    }

    [Fact]
    public async Task A_step_that_takes_the_run_past_32_MiB_of_state_is_logged()
    {
        var chunk = new string('x', 3 * 1024 * 1024);
        var function = TestFunction.Define(async context =>
        {
            for (var i = 0; i < 11; i++)
            {
                await context.Step.RunAsync($"chunk-{i}", () => chunk);
            }

            return null;
        });
        var call = Call.New();
        for (var i = 0; i < 10; i++)
        {
            call = call.Data($"chunk-{i}", $"\"{chunk}\"");
        }

        var result = await _host.CallAsync(function, call);

        result.OpCodes.ShouldBe(["StepRun"]);
        var warning = _host.Logs.GetSnapshot().Where(static record => record.Id.Id == 171).ShouldHaveSingleItem();
        warning.Level.ShouldBe(LogLevel.Warning);
        warning.Message.ShouldContain("Step chunk-10 of run 01JRUN");
    }

    [Fact]
    public async Task A_run_that_finds_its_1001st_step_is_logged_once()
    {
        var function = TestFunction.Define(async context =>
        {
            for (var i = 0; i < 1_002; i++)
            {
                await context.Step.RunAsync($"step-{i}", () => i);
            }

            return null;
        });
        var call = Call.New();
        for (var i = 0; i < 1_001; i++)
        {
            call = call.Data($"step-{i}", "0");
        }

        await _host.CallAsync(function, call);

        var warning = _host.Logs.GetSnapshot().Where(static record => record.Id.Id == 170).ShouldHaveSingleItem();
        warning.Message.ShouldContain("reached step 1001, \"step-1000\"");
    }
}
