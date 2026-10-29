namespace Inngest.Tests.Execution;

// Function code that leaves the invocation's scheduler must still get correct results.
public sealed class ThreadingTests : IDisposable
{
    private readonly EngineHost _host = new();

    public void Dispose() => _host.Dispose();

    [Fact]
    public async Task Code_that_continues_off_the_scheduler_replays_correctly()
    {
        var function = TestFunction.Define(async context =>
        {
            var a = await context.Step.RunAsync("A", () => "A").ConfigureAwait(false);
            await Task.Delay(5).ConfigureAwait(false);
            var b = await context.Step.RunAsync("B", () => "B").ConfigureAwait(false);
            await Task.Delay(5).ConfigureAwait(false);
            return a + b;
        });

        var first = await _host.CallAsync(function, Call.New());
        var second = await _host.CallAsync(function, Call.New().Data("A", "\"A\""));
        var third = await _host.CallAsync(function, Call.New().Data("A", "\"A\"").Data("B", "\"B\""));

        first.DisplayNames.ShouldBe(["A"]);
        second.DisplayNames.ShouldBe(["B"]);
        second.OpCodes.ShouldBe(["StepRun"]);
        third.StatusCode.ShouldBe(200);
        third.Body.ShouldBe("\"AB\"");
    }

    [Fact]
    public async Task Steps_called_from_the_thread_pool_are_registered_on_the_scheduler()
    {
        var function = TestFunction.Define(async context =>
        {
            var results = await Task.WhenAll(
                Task.Run(() => context.Step.RunAsync("A", () => "A")),
                Task.Run(() => context.Step.RunAsync("B", () => "B")));
            return results[0] + results[1];
        });

        var result = await _host.CallAsync(function, Call.New().Data("A", "\"A\"").Data("B", "\"B\""));

        result.StatusCode.ShouldBe(200);
        result.Body.ShouldBe("\"AB\"");
    }

    [Fact]
    public async Task A_step_called_from_the_thread_pool_runs_right_away()
    {
        var function = TestFunction.Define(async context => await Task.Run(() => context.Step.RunAsync("A", () => "A")));

        var result = await _host.CallAsync(function, Call.New());

        result.OpCodes.ShouldBe(["StepRun"]);
        result.Op().GetProperty("data").GetString().ShouldBe("A");
    }

    [Fact]
    public async Task A_function_that_finishes_off_the_scheduler_is_noticed()
    {
        var function = TestFunction.Define(async _ =>
        {
            await Task.Delay(5).ConfigureAwait(false);
            return "done";
        });

        var result = await _host.CallAsync(function, Call.New());

        result.Body.ShouldBe("\"done\"");
    }

    [Fact]
    public async Task Steps_called_after_the_invocation_answered_never_complete()
    {
        var release = new TaskCompletionSource();
        var called = new TaskCompletionSource<Task<string>>();
        var function = TestFunction.Define(async context =>
        {
            _ = Task.Run(async () =>
            {
                await release.Task;
                called.SetResult(context.Step.RunAsync("late", () => "late"));
            });
            await context.Step.SleepAsync("sleep-a", TimeSpan.FromHours(1));
            return null;
        });

        var result = await _host.CallAsync(function, Call.New());
        release.SetResult();
        var late = await called.Task;
        await Task.Delay(20, TestContext.Current.CancellationToken);

        result.OpCodes.ShouldBe(["Sleep"]);
        late.IsCompleted.ShouldBeFalse();
    }

    [Fact]
    public async Task Abandoned_steps_never_run_catch_or_finally_blocks()
    {
        var ran = new List<string>();
        var function = TestFunction.Define(async context =>
        {
            try
            {
                await context.Step.SleepAsync("sleep-a", TimeSpan.FromHours(1));
                ran.Add("after");
            }
            catch
            {
                ran.Add("catch");
            }
            finally
            {
                ran.Add("finally");
            }

            return null;
        });

        var result = await _host.CallAsync(function, Call.New());
        GC.Collect();
        GC.WaitForPendingFinalizers();

        result.OpCodes.ShouldBe(["Sleep"]);
        ran.ShouldBeEmpty();
    }

    [Fact]
    public async Task Steps_cannot_be_called_from_inside_a_step()
    {
        var function = TestFunction.Define(async context =>
            await context.Step.RunAsync("outer", async () => await context.Step.RunAsync("inner", () => 1)));

        var result = await _host.CallAsync(function, Call.New());

        result.OpCodes.ShouldBe(["StepError"]);
        var error = result.Op().GetProperty("error");
        error.GetProperty("name").GetString().ShouldBe("InvalidOperationException");
        error.GetProperty("message").GetString()!.ShouldContain("\"inner\"");
        error.GetProperty("message").GetString()!.ShouldContain("\"outer\"");
    }

    [Fact]
    public async Task Nested_steps_are_detected_on_other_threads_too()
    {
        var function = TestFunction.Define(async context =>
            await context.Step.RunAsync("outer", () => Task.Run(() => context.Step.RunAsync("inner", () => 1))));

        var result = await _host.CallAsync(function, Call.New());

        result.Op().GetProperty("error").GetProperty("name").GetString().ShouldBe("InvalidOperationException");
    }

    [Fact]
    public async Task Code_that_keeps_yielding_does_not_hold_up_the_decision()
    {
        var function = TestFunction.Define(async context =>
        {
            _ = context.Step.RunAsync("A", () => "A");
            while (true)
            {
                await Task.Yield();
            }
        });

        var result = await _host.CallAsync(function, Call.New());

        result.OpCodes.ShouldBe(["StepRun"]);
    }

    [Fact]
    public async Task Cancelling_the_call_stops_waiting_for_the_function()
    {
        using var cancellation = new CancellationTokenSource();
        var never = new TaskCompletionSource();
        var function = TestFunction.Define(async _ =>
        {
            await never.Task;
            return null;
        });

        var pending = _host.Engine.ExecuteAsync(
            new() { Function = function, Body = Call.New().ToBytes(), StepId = "step", Services = _host.Services },
            cancellation.Token);
        await cancellation.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(() => pending);
    }
}
