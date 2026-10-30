using Inngest.Tests.Execution;
using Microsoft.Extensions.DependencyInjection;

namespace Inngest.Tests.Middleware;

// The per-hook cases of the TypeScript SDK's middleware suite: when each hook fires, when it must not, and what a
// hook that throws does to the run.
public sealed class MiddlewareHookTests
{
    private const int ObserverFailureEventId = 105;

    private readonly List<string> _log = [];

    private EngineHost Host(params InngestMiddleware[] middleware) => new(services =>
    {
        foreach (var instance in middleware)
        {
            services.AddSingleton(instance);
        }
    });

    [Fact]
    public async Task Run_start_is_reported_once_for_a_function_without_steps()
    {
        using var host = Host(new Recorder(_log));

        await host.CallAsync(TestFunction.Define(_ => Task.FromResult<object?>("hello")), Call.New());

        _log.ShouldBe(["OnMemoizationEnd", "OnRunStart", "OnRunComplete hello"]);
    }

    [Fact]
    public async Task Run_start_is_reported_even_when_the_function_throws()
    {
        using var host = Host(new Recorder(_log));

        var result = await host.CallAsync(TestFunction.Define(_ => throw new InvalidOperationException("fn error")), Call.New());

        result.StatusCode.ShouldBe(500);
        _log.Count(entry => entry == "OnRunStart").ShouldBe(1);
    }

    [Fact]
    public async Task Run_start_is_reported_once_for_a_run_that_takes_several_calls()
    {
        using var host = Host(new Recorder(_log));
        var function = TestFunction.Define(async context => await context.Step.RunAsync("my-step", () => "result"));

        await host.CallAsync(function, Call.New());
        await host.CallAsync(function, Call.New().Data("my-step", "\"result\""));

        _log.Count(entry => entry == "OnRunStart").ShouldBe(1);
    }

    [Fact]
    public async Task Run_start_is_not_reported_again_for_the_steps_the_first_call_planned()
    {
        using var host = Host(new Recorder(_log));
        var function = TestFunction.Define(async context =>
        {
            await Task.WhenAll(context.Step.RunAsync("a", () => "a"), context.Step.RunAsync("b", () => "b"));
            return null;
        });

        // The calls for the planned steps carry no memoized steps and attempt 0, like the first call.
        await host.CallAsync(function, Call.New());
        await host.CallAsync(function, Call.New().DisableImmediateExecution(), stepId: Call.Hash("a"));
        await host.CallAsync(function, Call.New().DisableImmediateExecution(), stepId: Call.Hash("b"));

        _log.Count(entry => entry == "OnRunStart").ShouldBe(1);
    }

    [Fact]
    public async Task Run_complete_is_not_reported_when_the_function_throws()
    {
        using var host = Host(new Recorder(_log));

        await host.CallAsync(TestFunction.Define(_ => throw new InvalidOperationException("fn error")), Call.New());

        _log.ShouldNotContain(entry => entry.StartsWith("OnRunComplete", StringComparison.Ordinal));
        _log.ShouldContain("OnRunError fn error final=False");
    }

    [Fact]
    public async Task Run_error_is_not_reported_when_the_function_succeeds()
    {
        using var host = Host(new Recorder(_log));

        await host.CallAsync(TestFunction.Define(_ => Task.FromResult<object?>("fine")), Call.New());

        _log.ShouldNotContain(entry => entry.StartsWith("OnRunError", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_function_that_throws_after_its_steps_completed_reports_a_run_error()
    {
        using var host = Host(new Recorder(_log));
        var function = TestFunction.Define(async context =>
        {
            await context.Step.RunAsync("a", () => 1);
            throw new InvalidOperationException("after the step");
        });

        await host.CallAsync(function, Call.New().Data("a", "1"));

        _log.ShouldContain("OnRunError after the step final=False");
    }

    [Fact]
    public async Task A_step_that_fails_for_good_is_a_step_error_and_then_a_final_run_error()
    {
        using var host = Host(new Recorder(_log));
        var function = TestFunction.Define(async context => await context.Step.RunAsync<int>("a", _ => throw new NonRetriableException("bad input")));

        var first = await host.CallAsync(function, Call.New());
        var second = await host.CallAsync(function, Call.New().Failed("a", "NonRetriableException", "bad input"));

        first.OpCodes.ShouldBe(["StepFailed"]);
        second.StatusCode.ShouldBe(400);
        _log.Where(entry => entry.Contains("Error", StringComparison.Ordinal))
            .ShouldBe(["OnStepError a bad input final=True", "OnRunError bad input final=True"]);
    }

    [Fact]
    public async Task A_failed_step_is_not_reported_as_completed()
    {
        using var host = Host(new Recorder(_log));
        var function = TestFunction.Define(async context => await context.Step.RunAsync<int>("a", _ => throw new InvalidOperationException("step error")));

        await host.CallAsync(function, Call.New());

        _log.ShouldContain("OnStepError a step error final=False");
        _log.ShouldNotContain(entry => entry.StartsWith("OnStepComplete", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_step_error_is_not_reported_when_steps_succeed()
    {
        using var host = Host(new Recorder(_log));
        var function = TestFunction.Define(async context =>
        {
            await context.Step.RunAsync("a", () => 1);
            return await context.Step.RunAsync("b", () => 2);
        });

        await host.CallAsync(function, Call.New());
        await host.CallAsync(function, Call.New().Data("a", "1"));

        _log.Where(entry => entry.StartsWith("OnStepComplete", StringComparison.Ordinal)).ShouldBe(["OnStepComplete a 1", "OnStepComplete b 2"]);
        _log.ShouldNotContain(entry => entry.StartsWith("OnStepError", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_memoized_step_is_not_reported_as_started_completed_or_failed()
    {
        using var host = Host(new Recorder(_log));
        var function = TestFunction.Define(async context => await context.Step.RunAsync("a", () => "never runs"));

        await host.CallAsync(function, Call.New().Data("a", "\"done\""));

        _log.ShouldNotContain(entry => entry.StartsWith("OnStep", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Observers_that_throw_never_change_the_outcome_of_the_run()
    {
        using var host = Host(new FailingObserver());
        var completed = TestFunction.Define(async context => await context.Step.RunAsync("a", () => "A"));
        var failing = TestFunction.Define(_ => throw new InvalidOperationException("fn error"));
        var stepFailing = TestFunction.Define(async context => await context.Step.RunAsync<int>("a", _ => throw new InvalidOperationException("step error")));

        var finished = await host.CallAsync(completed, Call.New().Data("a", "\"A\""));
        var failed = await host.CallAsync(failing, Call.New());
        var stepFailed = await host.CallAsync(stepFailing, Call.New());

        finished.StatusCode.ShouldBe(200);
        finished.Body.ShouldBe("\"A\"");
        failed.StatusCode.ShouldBe(500);
        failed.Json.GetProperty("message").GetString().ShouldBe("fn error");
        stepFailed.OpCodes.ShouldBe(["StepError"]);
        host.Logs.GetSnapshot().Where(record => record.Id.Id == ObserverFailureEventId).Select(record => record.Message).ShouldContain(
            "Middleware FailingObserver failed in OnRunCompleteAsync; the run continues.");
        host.Logs.GetSnapshot().Where(record => record.Id.Id == ObserverFailureEventId).Select(record => record.Message).ShouldContain(
            "Middleware FailingObserver failed in OnRunErrorAsync; the run continues.");
        host.Logs.GetSnapshot().Where(record => record.Id.Id == ObserverFailureEventId).Select(record => record.Message).ShouldContain(
            "Middleware FailingObserver failed in OnStepErrorAsync; the run continues.");
    }

    [Fact]
    public async Task Hooks_follow_a_run_with_parallel_steps_across_its_calls()
    {
        using var host = Host(new Recorder(_log, timeline: true));
        var function = TestFunction.Define(async context =>
        {
            _log.Add("fn: top");
            await context.Step.RunAsync("step-1", () =>
            {
                _log.Add("step-1: inside");
                return "step-1: output";
            });
            var run = context.Step.RunAsync("step-2-a", () =>
            {
                _log.Add("step-2-a: inside");
                return "step-2-a: output";
            });
            var sleep = context.Step.SleepAsync("step-2-b", TimeSpan.FromSeconds(1));
            await Task.WhenAll(run, sleep);
            await context.Step.RunAsync("step-3", () =>
            {
                _log.Add("step-3: inside");
                return "step-3: output";
            });
            _log.Add("fn: bottom");
            return null;
        });

        await host.CallAsync(function, Call.New());
        await host.CallAsync(function, Call.New().Data("step-1", "\"step-1: output\""));
        await host.CallAsync(function, Call.New().Data("step-1", "\"step-1: output\"").DisableImmediateExecution(), stepId: Call.Hash("step-2-a"));
        await host.CallAsync(function, Call.New().Data("step-1", "\"step-1: output\"").Data("step-2-a", "\"step-2-a: output\"").DisableImmediateExecution());
        await host.CallAsync(
            function,
            Call.New().Data("step-1", "\"step-1: output\"").Data("step-2-a", "\"step-2-a: output\"").Memo("step-2-b", "null"));
        await host.CallAsync(
            function,
            Call.New()
                .Data("step-1", "\"step-1: output\"")
                .Data("step-2-a", "\"step-2-a: output\"")
                .Memo("step-2-b", "null")
                .Data("step-3", "\"step-3: output\""));

        _log.ShouldBe(
        [
            // 1: runs step-1 as soon as it is found
            "onMemoizationEnd", "onRunStart", "fn: top", "onStepStart", "step-1: inside", "onStepComplete",

            // 2: finds the two parallel steps and reports them without running either
            "fn: top", "onMemoizationEnd",

            // 3: Inngest asks for step-2-a by itself
            "fn: top", "onMemoizationEnd", "onStepStart", "step-2-a: inside", "onStepComplete",

            // 4: the sleep is reported
            "fn: top", "onMemoizationEnd",

            // 5: both are done, so step-3 runs
            "fn: top", "onMemoizationEnd", "onStepStart", "step-3: inside", "onStepComplete",

            // 6: everything is memoized
            "fn: top", "onMemoizationEnd", "fn: bottom", "onRunComplete",
        ]);
    }

    private sealed class Recorder(List<string> log, bool timeline = false) : InngestMiddleware
    {
        public override ValueTask OnMemoizationEndAsync(FunctionContext context) => Add(timeline ? "onMemoizationEnd" : "OnMemoizationEnd");

        public override ValueTask OnRunStartAsync(FunctionContext context) => Add(timeline ? "onRunStart" : "OnRunStart");

        public override ValueTask OnRunCompleteAsync(FunctionContext context, object? output) =>
            Add(timeline ? "onRunComplete" : $"OnRunComplete {output}");

        public override ValueTask OnRunErrorAsync(FunctionContext context, Exception exception, bool isFinalAttempt) =>
            Add($"OnRunError {exception.Message} final={isFinalAttempt}");

        public override ValueTask OnStepStartAsync(FunctionContext context, StepInfo step) => Add(timeline ? "onStepStart" : $"OnStepStart {step.Id}");

        public override ValueTask OnStepCompleteAsync(FunctionContext context, StepInfo step, object? output) =>
            Add(timeline ? "onStepComplete" : $"OnStepComplete {step.Id} {output}");

        public override ValueTask OnStepErrorAsync(FunctionContext context, StepInfo step, Exception exception, bool isFinalAttempt) =>
            Add($"OnStepError {step.Id} {exception.Message} final={isFinalAttempt}");

        private ValueTask Add(string entry)
        {
            lock (log)
            {
                log.Add(entry);
            }

            return ValueTask.CompletedTask;
        }
    }

    private sealed class FailingObserver : InngestMiddleware
    {
        public override ValueTask OnRunCompleteAsync(FunctionContext context, object? output) => throw new InvalidOperationException("observer");

        public override ValueTask OnRunErrorAsync(FunctionContext context, Exception exception, bool isFinalAttempt) =>
            throw new InvalidOperationException("observer");

        public override ValueTask OnStepErrorAsync(FunctionContext context, StepInfo step, Exception exception, bool isFinalAttempt) =>
            throw new InvalidOperationException("observer");
    }
}
