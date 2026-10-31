using Inngest.Functions;
using Inngest.Tests.Execution;

namespace Inngest.Tests.FanOut;

// A fan-out called from code that has left the invocation's scheduler, after ConfigureAwait(false) or inside Task.Run.
// The engine tells that the function is idle on its scheduler, so a lane that went on somewhere else would start its next
// item while the engine decides what to report. Whichever way the fan-out is called, the same record has to give the
// same answer on every replay, and an item must not start after a failure that the record has before it.
public sealed class FanOutOffSchedulerTests : IDisposable
{
    private const int Replays = 1000;

    private static readonly string[] Nine = ["a", "b", "c", "d", "e", "f", "g", "h", "i"];

    private readonly EngineHost _host = new();

    public enum Caller
    {
        OnScheduler,
        AfterConfigureAwaitFalse,
        InTaskRun,
    }

    public void Dispose() => _host.Dispose();

    [Theory]
    [InlineData(Caller.OnScheduler)]
    [InlineData(Caller.AfterConfigureAwaitFalse)]
    [InlineData(Caller.InTaskRun)]
    public async Task The_window_found_after_results_recorded_out_of_order_is_the_same_on_every_replay(Caller caller)
    {
        // Two windows of three finished out of order, 2 0 1 and then 5 3 4, so the third window is found while the
        // results of the second are handed back.
        var recorded = Call.New()
            .Data("before", "0")
            .Data("charge[2]", "\"C\"").Data("charge[0]", "\"A\"").Data("charge[1]", "\"B\"")
            .Data("charge[5]", "\"F\"").Data("charge[3]", "\"D\"").Data("charge[4]", "\"E\"")
            .DisableImmediateExecution();

        var answers = await ReplayAsync(Window(caller), recorded);

        answers.ShouldBe(new Dictionary<string, int> { ["206 charge[6],charge[7],charge[8]"] = Replays });
    }

    [Theory]
    [InlineData(Caller.OnScheduler)]
    [InlineData(Caller.AfterConfigureAwaitFalse)]
    [InlineData(Caller.InTaskRun)]
    public async Task The_failure_recorded_first_is_the_one_thrown_and_no_item_starts_after_it_on_every_replay(Caller caller)
    {
        // Item 5 failed before item 3 did. The window is three items, so items 3 to 5 are found while the results of 0
        // to 2 are handed back, and item 6 would start if the failure of item 5 were seen late.
        var recorded = Call.New()
            .Data("before", "0")
            .Data("charge[2]", "\"C\"").Data("charge[0]", "\"A\"").Data("charge[1]", "\"B\"")
            .Failed("charge[5]", "Error", "five").Failed("charge[3]", "Error", "three").Data("charge[4]", "\"E\"")
            .DisableImmediateExecution();

        var answers = await ReplayAsync(Failing(caller), recorded);

        answers.ShouldBe(new Dictionary<string, int> { ["200 \"charge[5]\""] = Replays });
    }

    [Fact]
    public async Task The_mode_of_an_enclosing_ParallelAsync_reaches_the_steps_of_a_fan_out_called_off_the_scheduler()
    {
        var function = TestFunction.Define(async context =>
        {
            await context.Step.RunAsync("before", () => 0).ConfigureAwait(false);
            return (object?)await context.Step.ParallelAsync(
                ParallelMode.Race,
                () => context.Step.MapAsync("charge", Nine[..2], (item, _) => Task.FromResult(item)));
        });

        var result = await _host.CallAsync(function, Call.New().Data("before", "0"));

        result.DisplayNames.ShouldBe(["charge[0]", "charge[1]"]);
        result.Ops.ShouldAllBe(op => op.GetProperty("opts").GetProperty("parallelMode").GetString() == "race");
    }

    [Fact]
    public async Task A_fan_out_called_inside_the_handler_of_a_step_is_refused_as_any_step_there_is()
    {
        var function = TestFunction.Define(async context =>
        {
            await context.Step.RunAsync("outer", async () =>
            {
                await context.Step.MapAsync("inner", Nine[..2], (item, _) => Task.FromResult(item));
                return 1;
            });
            return null;
        });

        var result = await _host.CallAsync(function, Call.New());

        result.OpCodes.ShouldBe(["StepError"]);
        result.Op().GetProperty("error").GetProperty("message").GetString()!.ShouldContain("was called inside the handler of step \"outer\"");
    }

    // The answers to the same call, counted, so that a failure shows how often each of them came.
    private async Task<Dictionary<string, int>> ReplayAsync(FunctionDefinition function, Call recorded)
    {
        var answers = new Dictionary<string, int>();
        for (var i = 0; i < Replays; i++)
        {
            var result = await _host.CallAsync(function, recorded);
            var answer = result.StatusCode == 206 ? $"206 {string.Join(",", result.DisplayNames)}" : $"{result.StatusCode} {result.Body}";
            answers[answer] = answers.GetValueOrDefault(answer) + 1;
        }

        return answers;
    }

    private static FunctionDefinition Window(Caller caller) =>
        TestFunction.Define(async context =>
        {
            Task<string[]> Fan() => context.Step.MapAsync(
                "charge",
                Nine,
                (item, _) => Task.FromResult(item.ToUpperInvariant()),
                new FanOutOptions { MaxParallelism = 3 });

            return (object?)await CallFan(context, caller, Fan);
        });

    private static FunctionDefinition Failing(Caller caller) =>
        TestFunction.Define(async context =>
        {
            Task<string[]> Fan() => context.Step.MapAsync(
                "charge",
                Nine,
                (item, _) => Task.FromResult(item.ToUpperInvariant()),
                new FanOutOptions { MaxParallelism = 3 });

            try
            {
                await CallFan(context, caller, Fan);
                return "completed";
            }
            catch (StepFailedException exception)
            {
                return exception.StepId;
            }
        });

    // Calls the fan-out from the scheduler, from code that continues on the thread pool after a memoized step, or from
    // inside Task.Run.
    private static async Task<string[]> CallFan(FunctionContext context, Caller caller, Func<Task<string[]>> fan)
    {
        switch (caller)
        {
            case Caller.AfterConfigureAwaitFalse:
                // The memoized result is handed back on the scheduler; the code goes on on the thread pool.
                await context.Step.RunAsync("before", () => 0).ConfigureAwait(false);
                return await fan().ConfigureAwait(false);
            case Caller.InTaskRun:
                await context.Step.RunAsync("before", () => 0);
                return await Task.Run(fan);
            default:
                await context.Step.RunAsync("before", () => 0);
                return await fan();
        }
    }
}
