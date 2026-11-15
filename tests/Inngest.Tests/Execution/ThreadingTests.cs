using System.Collections.Concurrent;
using System.Diagnostics;
using Inngest.Execution;
using Inngest.Functions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Inngest.Tests.Execution;

// Function code that leaves the invocation's scheduler must still get correct results.
public sealed class ThreadingTests : IDisposable
{
    // Short, so that the tests of a call that outlives it do not wait long. The engine's clock never moves in them: the
    // grace runs in real time.
    private static readonly TimeSpan Grace = TimeSpan.FromMilliseconds(200);

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
    public async Task A_cancelled_call_stops_waiting_for_work_of_the_function_after_a_grace_period()
    {
        using var host = new EngineHost(cancelledCallGrace: Grace);
        using var cancellation = new CancellationTokenSource();
        var never = new TaskCompletionSource();
        var function = TestFunction.Define(async _ =>
        {
            await never.Task;
            return null;
        });

        var pending = Execute(host, function, cancellation.Token);
        var waited = Stopwatch.StartNew();
        await cancellation.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        waited.Elapsed.ShouldBeGreaterThan(Grace / 2);
        var record = host.Logs.GetSnapshot().Where(record => record.Id.Id == 111).ShouldHaveSingleItem();
        record.Level.ShouldBe(LogLevel.Warning);
        record.Category.ShouldBe("Inngest.Function.fn");
        record.Message.ShouldStartWith("Run 01JRUN of function fn was still waiting on work of its own 00:00:00.2000000 after the call was cancelled");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_cancelled_call_ends_only_once_the_work_the_function_waits_on_has_finished(bool forOneStep)
    {
        var timeline = new Timeline();
        using var host = new EngineHost(services => services.AddScoped(_ => new ScopedResource(timeline)));
        using var cancellation = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource();
        var function = TestFunction.Define(async context =>
        {
            context.Services.GetRequiredService<ScopedResource>();
            _ = timeline.ProbeAsync();
            started.SetResult();

            // Work of the function's own that ignores the token, such as a query on the call's DbContext.
            await release.Task;
            timeline.Add("work done");
            return await context.Step.RunAsync("next", () => "next");
        });

        var pending = ExecuteInScopeAsync(host, function, cancellation.Token, forOneStep ? Call.Hash("next") : "step");
        await started.Task;
        await cancellation.CancelAsync();
        await timeline.CallWaitsOrEndedAsync();
        release.SetResult();

        // Once the function reaches a step, none of its code runs any more, so the call ends without the grace period.
        await Should.ThrowAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        timeline.Events.ShouldBe(["work done", "disposed"]);
        host.Logs.GetSnapshot().ShouldNotContain(record => record.Id.Id == 111);
    }

    [Fact]
    public async Task A_cancelled_call_for_a_step_the_function_does_not_reach_ends_at_once()
    {
        using var host = new EngineHost();
        using var cancellation = new CancellationTokenSource();
        var function = TestFunction.Define(async context =>
        {
            await context.Step.RunAsync("A", () => "A");
            return await context.Step.RunAsync("B", () => "B");
        });

        var pending = host.Engine.ExecuteAsync(
            new() { Function = function, Body = Call.New().ToBytes(), StepId = Call.Hash("B"), Services = host.Services },
            cancellation.Token);
        await host.Time.TimersCreated(1).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await cancellation.CancelAsync();

        // The function waits on A, which runs in a call of its own, so none of its code runs in this call any more.
        await Should.ThrowAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        host.Logs.GetSnapshot().ShouldNotContain(record => record.Id.Id == 111);
    }

    [Fact]
    public async Task A_step_can_await_a_task_the_function_started()
    {
        var function = TestFunction.Define(async context =>
        {
            var loading = LoadAsync();
            return await context.Step.RunAsync("use", async () => await loading + "!");
        });

        // Without the scheduler running during the step, the call never answers.
        var result = await _host.CallAsync(function, Call.New()).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        result.OpCodes.ShouldBe(["StepRun"]);
        result.Op().GetProperty("data").GetString().ShouldBe("loaded!");

        // Resumes on the invocation's scheduler, as app code without ConfigureAwait(false) does.
        static async Task<string> LoadAsync()
        {
            await Task.Delay(5);
            return "loaded";
        }
    }

    [Fact]
    public async Task A_cancelled_call_stops_waiting_for_a_step_that_ignores_the_token_after_a_grace_period()
    {
        using var host = new EngineHost(cancelledCallGrace: Grace);
        using var cancellation = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var never = new TaskCompletionSource<string>();
        var function = TestFunction.Define(async context => await context.Step.RunAsync("stuck", async () =>
        {
            started.SetResult();
            return await never.Task;
        }));

        var pending = Execute(host, function, cancellation.Token);
        await started.Task;
        var waited = Stopwatch.StartNew();
        await cancellation.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        waited.Elapsed.ShouldBeGreaterThan(Grace / 2);
        var record = host.Logs.GetSnapshot().Where(record => record.Id.Id == 110).ShouldHaveSingleItem();
        record.Level.ShouldBe(LogLevel.Warning);
        record.Message.ShouldStartWith("Step stuck of run 01JRUN of function fn was still running 00:00:00.2000000 after the call was cancelled");
    }

    [Fact]
    public async Task A_cancelled_call_ends_only_once_the_step_it_started_has_finished()
    {
        var timeline = new Timeline();
        using var host = new EngineHost(services => services.AddScoped(_ => new ScopedResource(timeline)));
        using var cancellation = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource();
        var function = TestFunction.Define(async context =>
        {
            _ = timeline.ProbeAsync();
            return await context.Step.RunAsync("slow", async () =>
            {
                context.Services.GetRequiredService<ScopedResource>();
                started.SetResult();
                await release.Task;
                timeline.Add("step done");
                return "done";
            });
        });

        var pending = ExecuteInScopeAsync(host, function, cancellation.Token);
        await started.Task;
        await cancellation.CancelAsync();
        await timeline.CallWaitsOrEndedAsync();
        release.SetResult();

        // The call still ends as cancelled, though the step finished.
        await Should.ThrowAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        timeline.Events.ShouldBe(["step done", "disposed"]);
        host.Logs.GetSnapshot().ShouldNotContain(record => record.Id.Id == 110);
    }

    [Fact]
    public async Task A_step_that_fails_after_its_call_was_cancelled_is_not_reported_as_unobserved()
    {
        using var host = new EngineHost(cancelledCallGrace: Grace);
        var message = "late failure " + Guid.NewGuid();
        var unobserved = await CountUnobservedAsync(message, async () =>
        {
            using var cancellation = new CancellationTokenSource();
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource();
            var function = TestFunction.Define(async context => await context.Step.RunAsync<string>("late", async () =>
            {
                started.SetResult();
                await release.Task;
                throw new InvalidOperationException(message);
            }));

            var pending = Execute(host, function, cancellation.Token);
            await started.Task;
            await cancellation.CancelAsync();
            await Should.ThrowAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
            release.SetResult();
        });

        unobserved.ShouldBe(0);
    }

    [Fact]
    public async Task A_function_that_fails_after_its_call_was_cancelled_is_not_reported_as_unobserved()
    {
        using var host = new EngineHost(cancelledCallGrace: Grace);
        var message = "late failure " + Guid.NewGuid();
        var unobserved = await CountUnobservedAsync(message, async () =>
        {
            using var cancellation = new CancellationTokenSource();
            var release = new TaskCompletionSource();
            var function = TestFunction.Define(async _ =>
            {
                await release.Task.ConfigureAwait(false);
                throw new InvalidOperationException(message);
            });

            var pending = Execute(host, function, cancellation.Token);
            await cancellation.CancelAsync();
            await Should.ThrowAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
            release.SetResult();
        });

        unobserved.ShouldBe(0);
    }

    [Fact]
    public async Task Code_that_holds_the_function_thread_while_work_waits_for_it_is_logged_once()
    {
        using var host = new EngineHost(heldThreadTimeout: TimeSpan.FromMilliseconds(50));
        var function = TestFunction.Define(async _ =>
        {
            var loading = LoadAsync();

            // Holds the thread as loading.Result would, which would never return, until the watchdog has spoken.
            SpinWait.SpinUntil(() => host.Logs.GetSnapshot().Any(record => record.Id.Id == 112), TimeSpan.FromSeconds(10));
            return await loading;
        });

        var result = await host.CallAsync(function, Call.New());

        result.Body.ShouldBe("\"loaded\"");
        var record = host.Logs.GetSnapshot().Where(record => record.Id.Id == 112).ShouldHaveSingleItem();
        record.Level.ShouldBe(LogLevel.Error);
        record.Category.ShouldBe("Inngest.Function.fn");
        record.Message.ShouldStartWith("Run 01JRUN of function fn has held its thread for 00:00:00.0500000 while work for it waits.");

        // Resumes on the invocation's scheduler, as app code without ConfigureAwait(false) does.
        static async Task<string> LoadAsync()
        {
            await Task.Delay(1);
            return "loaded";
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Code_that_blocks_on_a_step_it_found_is_logged(bool memoized)
    {
        using var host = new EngineHost(heldThreadTimeout: TimeSpan.FromMilliseconds(50));
        bool? completedWhileHeld = null;
        var function = TestFunction.Define(async context =>
        {
            var step = context.Step.RunAsync("A", () => "A");

            // Holds the thread as step.Result would, which would never return, until the watchdog has spoken.
            SpinWait.SpinUntil(() => host.Logs.GetSnapshot().Any(record => record.Id.Id == 112), TimeSpan.FromSeconds(10));
            completedWhileHeld = step.IsCompleted;
            return await step;
        });

        var result = await host.CallAsync(function, memoized ? Call.New().Data("A", "\"A\"") : Call.New());

        completedWhileHeld.ShouldBe(false);
        if (memoized)
        {
            result.Body.ShouldBe("\"A\"");
        }
        else
        {
            result.OpCodes.ShouldBe(["StepRun"]);
        }

        host.Logs.GetSnapshot().Where(record => record.Id.Id == 112).ShouldHaveSingleItem().Level.ShouldBe(LogLevel.Error);
    }

    [Fact]
    public async Task Code_that_blocks_on_a_step_whose_result_is_handed_back_later_is_logged()
    {
        using var host = new EngineHost(heldThreadTimeout: TimeSpan.FromMilliseconds(50));
        var function = TestFunction.Define(async context =>
        {
            var a = context.Step.RunAsync("A", () => "A");
            var b = context.Step.RunAsync("B", () => "B");

            // B completed first, so A's result is handed back only once this code lets go of the thread.
            var first = await b;
            SpinWait.SpinUntil(() => host.Logs.GetSnapshot().Any(record => record.Id.Id == 112), TimeSpan.FromSeconds(10));
            return first + await a;
        });

        var result = await host.CallAsync(function, Call.New().Data("B", "\"B\"").Data("A", "\"A\""));

        result.Body.ShouldBe("\"BA\"");
        host.Logs.GetSnapshot().Where(record => record.Id.Id == 112).ShouldHaveSingleItem();
    }

    [Fact]
    public async Task Code_that_holds_the_function_thread_after_its_steps_were_handed_back_is_not_logged()
    {
        using var host = new EngineHost(heldThreadTimeout: TimeSpan.FromMilliseconds(20));
        var function = TestFunction.Define(async context =>
        {
            var a = await context.Step.RunAsync("A", () => "A");
            Thread.Sleep(200);
            return a;
        });

        var result = await host.CallAsync(function, Call.New().Data("A", "\"A\""));

        result.Body.ShouldBe("\"A\"");
        host.Logs.GetSnapshot().ShouldNotContain(record => record.Id.Id == 112);
    }

    [Fact]
    public async Task Code_that_holds_the_function_thread_while_nothing_waits_for_it_is_not_logged()
    {
        using var host = new EngineHost(heldThreadTimeout: TimeSpan.FromMilliseconds(20));
        var function = TestFunction.Define(_ =>
        {
            Thread.Sleep(200);
            return Task.FromResult<object?>("done");
        });

        var result = await host.CallAsync(function, Call.New());

        result.Body.ShouldBe("\"done\"");
        host.Logs.GetSnapshot().ShouldNotContain(record => record.Id.Id == 112);
    }

    [Fact]
    public async Task Work_the_function_started_still_finishes_after_the_call_answered()
    {
        var release = new TaskCompletionSource();
        var shared = new TaskCompletionSource<Task<string>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var function = TestFunction.Define(async context =>
        {
            // Like a task a singleton caches: started here, awaited by whoever needs it later.
            shared.SetResult(LoadAsync(release.Task));
            await context.Step.SleepAsync("wait", TimeSpan.FromHours(1));
            return null;
        });

        var result = await _host.CallAsync(function, Call.New());
        release.SetResult();
        var loaded = await (await shared.Task).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        result.OpCodes.ShouldBe(["Sleep"]);
        loaded.ShouldBe("loaded");

        static async Task<string> LoadAsync(Task release)
        {
            await release;
            return "loaded";
        }
    }

    private static Task<ExecutionResult> Execute(EngineHost host, FunctionDefinition function, CancellationToken cancellationToken) =>
        host.Engine.ExecuteAsync(
            new() { Function = function, Body = Call.New().ToBytes(), StepId = "step", Services = host.Services },
            cancellationToken);

    // As a host serves a call: the call's services come from a scope that ends with the call.
    private static async Task ExecuteInScopeAsync(
        EngineHost host,
        FunctionDefinition function,
        CancellationToken cancellationToken,
        string stepId = "step")
    {
        await using var scope = host.Services.CreateAsyncScope();
        using var result = await host.Engine.ExecuteAsync(
            new() { Function = function, Body = Call.New().ToBytes(), StepId = stepId, Services = scope.ServiceProvider },
            cancellationToken);
    }

    // How many tasks failed with the message without anyone observing it, once the scenario has run and the garbage
    // collector has finalized what it left behind.
    private static async Task<int> CountUnobservedAsync(string message, Func<Task> scenario)
    {
        var count = 0;
        void OnUnobserved(object? sender, UnobservedTaskExceptionEventArgs args)
        {
            if (args.Exception.Flatten().InnerExceptions.Any(exception => exception.Message == message))
            {
                Interlocked.Increment(ref count);
            }
        }

        TaskScheduler.UnobservedTaskException += OnUnobserved;
        try
        {
            await scenario();
            for (var i = 0; i < 20 && Volatile.Read(ref count) == 0; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                await Task.Delay(10, TestContext.Current.CancellationToken);
            }

            return Volatile.Read(ref count);
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= OnUnobserved;
        }
    }

    private sealed class ScopedResource(Timeline timeline) : IDisposable
    {
        public void Dispose() => timeline.Disposed();
    }

    // What happened, in order, to a cancelled call's scope and to the code that uses it. The probe, started by the
    // function's code, tells whether the call still runs that code after the cancellation, as it does while it waits for
    // code it started, or has ended, so a test knows when to let that code finish without guessing how long the call takes.
    private sealed class Timeline
    {
        private readonly ConcurrentQueue<string> _events = new();
        private readonly TaskCompletionSource _disposed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _poke = new();
        private readonly TaskCompletionSource<bool> _resumedInCall = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string[] Events => [.. _events];

        public void Add(string @event) => _events.Enqueue(@event);

        public void Disposed()
        {
            Add("disposed");
            _disposed.TrySetResult();
        }

        public async Task ProbeAsync()
        {
            var call = SynchronizationContext.Current;
            await _poke.Task;
            _resumedInCall.SetResult(SynchronizationContext.Current == call);
        }

        // Returns while the call still runs the function's code, or once the call has ended and disposed its scope.
        public async Task CallWaitsOrEndedAsync()
        {
            _poke.SetResult();
            if (!await _resumedInCall.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken))
            {
                await _disposed.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            }
        }
    }
}
