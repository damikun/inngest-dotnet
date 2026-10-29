using Inngest.Functions;
using Inngest.Tests.Execution;
using Microsoft.Extensions.DependencyInjection;

namespace Inngest.Tests.Middleware;

public sealed class MiddlewareTests : IDisposable
{
    private readonly List<string> _log = [];
    private readonly EngineHost _host;

    public MiddlewareTests()
    {
        _host = new EngineHost(services =>
        {
            services.AddSingleton<InngestMiddleware>(new RecordingMiddleware("mw1", _log));
            services.AddSingleton<InngestMiddleware>(new RecordingMiddleware("mw2", _log));
        });
    }

    public void Dispose() => _host.Dispose();

    // The timeline of the TypeScript SDK's allHooks test, without checkpointing: a run step and then a sleep.
    [Fact]
    public async Task Hooks_run_in_order_when_a_step_executes()
    {
        await _host.CallAsync(RunThenSleep(), Call.New());

        _log.ShouldBe(
        [
            "mw1.OnMemoizationEnd", "mw2.OnMemoizationEnd",
            "mw1.OnRunStart", "mw2.OnRunStart",
            "mw1.WrapFunction:before", "mw2.WrapFunction:before",
            "mw1.WrapStep:before my-step new", "mw2.WrapStep:before my-step new",
            "mw1.OnStepStart my-step", "mw2.OnStepStart my-step",
            "mw1.WrapStepHandler:before", "mw2.WrapStepHandler:before",
            "handler",
            "mw2.WrapStepHandler:after", "mw1.WrapStepHandler:after",
            "mw1.OnStepComplete result", "mw2.OnStepComplete result",
        ]);
    }

    [Fact]
    public async Task Hooks_run_in_order_when_a_memoized_step_is_replayed()
    {
        await _host.CallAsync(RunThenSleep(), Call.New().Data("my-step", "\"result\""));

        _log.ShouldBe(
        [
            "mw1.WrapFunction:before", "mw2.WrapFunction:before",
            "mw1.OnMemoizationEnd", "mw2.OnMemoizationEnd",
            "mw1.WrapStep:before my-step memoized", "mw2.WrapStep:before my-step memoized",
            "mw2.WrapStep:after result", "mw1.WrapStep:after result",
        ]);
    }

    [Fact]
    public async Task Hooks_run_in_order_when_the_run_completes()
    {
        await _host.CallAsync(RunThenSleep(), Call.New().Data("my-step", "\"result\"").Memo("my-sleep", "null"));

        _log.ShouldBe(
        [
            "mw1.WrapFunction:before", "mw2.WrapFunction:before",
            "mw1.WrapStep:before my-step memoized", "mw2.WrapStep:before my-step memoized",
            "mw2.WrapStep:after result", "mw1.WrapStep:after result",
            "mw1.OnMemoizationEnd", "mw2.OnMemoizationEnd",
            "mw1.WrapStep:before my-sleep memoized", "mw2.WrapStep:before my-sleep memoized",
            "mw2.WrapStep:after null", "mw1.WrapStep:after null",
            "mw2.WrapFunction:after", "mw1.WrapFunction:after",
            "mw1.OnRunComplete done", "mw2.OnRunComplete done",
        ]);
    }

    [Fact]
    public async Task Run_start_is_only_reported_for_the_first_attempt_of_a_new_run()
    {
        var function = TestFunction.Define(_ => Task.FromResult<object?>(null));

        await _host.CallAsync(function, Call.New().Attempt(1));
        await _host.CallAsync(function, Call.New().Data("A", "1"));

        _log.ShouldNotContain("mw1.OnRunStart");
    }

    [Fact]
    public async Task Memoization_end_is_reported_once_per_call()
    {
        var function = TestFunction.Define(async context =>
        {
            await context.Step.RunAsync("A", () => 1);
            await Task.WhenAll(context.Step.RunAsync("B", () => 2), context.Step.RunAsync("C", () => 3));
            return null;
        });

        await _host.CallAsync(function, Call.New().Data("A", "1"));

        _log.Count(entry => entry == "mw1.OnMemoizationEnd").ShouldBe(1);
    }

    [Fact]
    public async Task Run_errors_are_reported_with_whether_they_are_final()
    {
        var function = TestFunction.Define(_ => throw new InvalidOperationException("boom"));

        await _host.CallAsync(function, Call.New().Attempt(0));
        await _host.CallAsync(function, Call.New().Attempt(3));

        _log.Where(entry => entry.StartsWith("mw1.OnRunError", StringComparison.Ordinal))
            .ShouldBe(["mw1.OnRunError boom retry", "mw1.OnRunError boom final"]);
    }

    [Fact]
    public async Task Step_errors_are_reported_with_whether_they_are_final()
    {
        var function = TestFunction.Define(async context => await context.Step.RunAsync<int>("A", _ => throw new InvalidOperationException("step boom")));

        await _host.CallAsync(function, Call.New().Attempt(0));
        await _host.CallAsync(function, Call.New().Attempt(3));

        _log.Where(entry => entry.StartsWith("mw1.OnStepError", StringComparison.Ordinal))
            .ShouldBe(["mw1.OnStepError A step boom retry", "mw1.OnStepError A step boom final"]);
    }

    [Fact]
    public async Task An_uncaught_step_failure_is_a_final_run_error()
    {
        var function = TestFunction.Define(async context => await context.Step.RunAsync("a", () => "never runs"));

        await _host.CallAsync(function, Call.New().Failed("a", "MyError", "step error").Attempt(0));

        _log.ShouldContain("mw1.OnRunError step error final");
    }

    [Fact]
    public async Task A_caught_and_replaced_step_failure_is_retried()
    {
        var function = TestFunction.Define(async context =>
        {
            try
            {
                await context.Step.RunAsync("a", () => "never runs");
            }
            catch (StepFailedException)
            {
                throw new InvalidOperationException("fn error");
            }

            return null;
        });

        await _host.CallAsync(function, Call.New().Failed("a", "Error", "step error").Attempt(0));

        _log.ShouldContain("mw1.OnRunError fn error retry");
    }

    [Fact]
    public async Task Step_start_is_not_reported_for_steps_inngest_carries_out()
    {
        var function = TestFunction.Define(async context =>
        {
            await context.Step.InvokeAsync<object>("invoke", new InvokeOptions("child-fn"));
            await context.Step.SleepAsync("sleep", TimeSpan.FromSeconds(1));
            await context.Step.WaitForEventAsync("wait", new WaitForEventOptions("never", TimeSpan.FromSeconds(1)));
            return null;
        });

        await _host.CallAsync(function, Call.New());
        await _host.CallAsync(function, Call.New().Data("invoke", "null"));
        await _host.CallAsync(function, Call.New().Data("invoke", "null").Memo("sleep", "null"));
        await _host.CallAsync(function, Call.New().Data("invoke", "null").Memo("sleep", "null").Memo("wait", "null"));

        _log.ShouldNotContain(entry => entry.Contains("OnStepStart", StringComparison.Ordinal));
        _log.ShouldContain("mw1.OnRunComplete ");
    }

    [Fact]
    public async Task Middleware_is_resolved_per_call_in_registration_order()
    {
        using var host = new EngineHost(services =>
        {
            services.AddTransient<InngestMiddleware, CountingMiddleware>();
            services.AddSingleton<InngestMiddleware>(new RecordingMiddleware("last", _log));
        });
        CountingMiddleware.Created = 0;
        var function = TestFunction.Define(_ => Task.FromResult<object?>(null));

        await host.CallAsync(function, Call.New());
        await host.CallAsync(function, Call.New());

        CountingMiddleware.Created.ShouldBe(2);
        _log.ShouldContain("last.OnRunStart");
    }

    [Fact]
    public async Task A_failing_observer_is_logged_and_the_run_continues()
    {
        using var host = new EngineHost(services => services.AddSingleton<InngestMiddleware>(new ThrowingObserver()));
        var function = TestFunction.Define(async context => await context.Step.RunAsync("A", () => "A"));

        var result = await host.CallAsync(function, Call.New());

        result.OpCodes.ShouldBe(["StepRun"]);
        var failures = host.Logs.GetSnapshot().Where(record => record.Id.Id == 105).ToList();
        failures.Select(record => record.Message).ShouldBe(
        [
            "Middleware ThrowingObserver failed in OnMemoizationEndAsync; the run continues.",
            "Middleware ThrowingObserver failed in OnRunStartAsync; the run continues.",
            "Middleware ThrowingObserver failed in OnStepStartAsync; the run continues.",
            "Middleware ThrowingObserver failed in OnStepCompleteAsync; the run continues.",
        ]);
    }

    [Fact]
    public async Task Steps_called_from_an_observer_fail_instead_of_hanging_the_call()
    {
        using var host = new EngineHost(services => services.AddSingleton<InngestMiddleware>(new StepInObserver()));
        var function = TestFunction.Define(_ => Task.FromResult<object?>("done"));

        var result = await host.CallAsync(function, Call.New());

        result.Body.ShouldBe("\"done\"");
        var failure = host.Logs.GetSnapshot().Where(record => record.Id.Id == 105).ShouldHaveSingleItem();
        failure.Exception.ShouldBeOfType<InvalidOperationException>().Message.ShouldContain("OnRunStartAsync");
    }

    [Fact]
    public async Task Wrap_function_can_change_the_output_and_the_error()
    {
        using var host = new EngineHost(services => services.AddSingleton<InngestMiddleware>(new OutputMiddleware()));

        var output = await host.CallAsync(TestFunction.Define(_ => Task.FromResult<object?>("done")), Call.New());
        var error = await host.CallAsync(TestFunction.Define(_ => throw new InvalidOperationException("boom")), Call.New());

        output.Body.ShouldBe("\"DONE\"");
        error.StatusCode.ShouldBe(400);
        error.Json.GetProperty("name").GetString().ShouldBe("NonRetriableException");
    }

    [Fact]
    public async Task Wrap_step_handler_changes_what_is_sent_to_inngest()
    {
        using var host = new EngineHost(services => services.AddSingleton<InngestMiddleware>(new EncryptionMiddleware()));
        var function = TestFunction.Define(async context => await context.Step.RunAsync("A", () => "secret"));

        var result = await host.CallAsync(function, Call.New());

        result.Op().GetProperty("data").GetString().ShouldBe("encrypted:secret");
    }

    [Fact]
    public async Task Wrap_step_changes_what_the_function_receives()
    {
        using var host = new EngineHost(services => services.AddSingleton<InngestMiddleware>(new EncryptionMiddleware()));
        var function = TestFunction.Define(async context => await context.Step.RunAsync("A", () => "never runs"));

        var result = await host.CallAsync(function, Call.New().Data("A", "\"encrypted:secret\""));

        result.Body.ShouldBe("\"secret\"");
    }

    [Fact]
    public async Task Wrap_step_sees_memoized_failures_and_can_recover()
    {
        using var host = new EngineHost(services => services.AddSingleton<InngestMiddleware>(new EncryptionMiddleware()));
        var function = TestFunction.Define(async context => await context.Step.RunAsync("A", () => "never runs"));

        var result = await host.CallAsync(function, Call.New().Failed("A", "Error", "card declined"));

        result.Body.ShouldBe("\"recovered from card declined\"");
    }

    [Fact]
    public async Task Wrap_step_returning_the_wrong_type_fails_at_the_call_site()
    {
        using var host = new EngineHost(services => services.AddSingleton<InngestMiddleware>(new EncryptionMiddleware()));
        var function = TestFunction.Define(async context =>
        {
            try
            {
                await context.Step.RunAsync("count", () => 1);
                return "no error";
            }
            catch (InvalidCastException)
            {
                return "cast error";
            }
        });

        var result = await host.CallAsync(function, Call.New().Data("count", "1"));

        result.Body.ShouldBe("\"cast error\"");
    }

    [Fact]
    public async Task Steps_a_middleware_creates_in_wrap_step_skip_that_middleware()
    {
        var audited = new List<string>();
        using var host = new EngineHost(services => services.AddSingleton<InngestMiddleware>(new AuditingMiddleware(audited)));
        var function = TestFunction.Define(async context => await context.Step.RunAsync("A", () => "A"));

        var discovery = await host.CallAsync(function, Call.New());
        var replay = await host.CallAsync(function, Call.New().Data("audit-A", "\"audited\"").Data("A", "\"A\""));

        // The audit step runs first; it is not audited itself and the user's step waits behind it.
        discovery.DisplayNames.ShouldBe(["audit-A"]);
        replay.Body.ShouldBe("\"A\"");
        audited.ShouldBe(["A", "A"]);
        host.Logs.GetSnapshot().ShouldNotContain(record => record.Id.Id == 100);
    }

    [Fact]
    public async Task A_wrap_step_that_answers_without_next_keeps_the_step_from_being_reported()
    {
        using var host = new EngineHost(services => services.AddSingleton<InngestMiddleware>(new CachingMiddleware()));
        var function = TestFunction.Define(async context => await context.Step.RunAsync("cached", () => "computed"));

        var result = await host.CallAsync(function, Call.New());

        result.StatusCode.ShouldBe(200);
        result.Body.ShouldBe("\"from cache\"");
    }

    private FunctionDefinition RunThenSleep() => TestFunction.Define(async context =>
    {
        await context.Step.RunAsync("my-step", () =>
        {
            _log.Add("handler");
            return "result";
        });
        await context.Step.SleepAsync("my-sleep", TimeSpan.FromMinutes(1));
        return "done";
    });

    private sealed class RecordingMiddleware(string name, List<string> log) : InngestMiddleware
    {
        public override ValueTask OnRunStartAsync(FunctionContext context) => Record("OnRunStart");

        public override ValueTask OnMemoizationEndAsync(FunctionContext context) => Record("OnMemoizationEnd");

        public override ValueTask OnRunCompleteAsync(FunctionContext context, object? output) => Record($"OnRunComplete {output}");

        public override ValueTask OnRunErrorAsync(FunctionContext context, Exception exception, bool isFinalAttempt) =>
            Record($"OnRunError {exception.Message} {(isFinalAttempt ? "final" : "retry")}");

        public override ValueTask OnStepStartAsync(FunctionContext context, StepInfo step) => Record($"OnStepStart {step.Id}");

        public override ValueTask OnStepCompleteAsync(FunctionContext context, StepInfo step, object? output) =>
            Record($"OnStepComplete {output}");

        public override ValueTask OnStepErrorAsync(FunctionContext context, StepInfo step, Exception exception, bool isFinalAttempt) =>
            Record($"OnStepError {step.Id} {exception.Message} {(isFinalAttempt ? "final" : "retry")}");

        public override async Task<object?> WrapFunctionAsync(FunctionContext context, Func<Task<object?>> next)
        {
            await Record("WrapFunction:before");
            var output = await next();
            await Record("WrapFunction:after");
            return output;
        }

        public override async Task<object?> WrapStepAsync(FunctionContext context, StepInfo step, Func<Task<object?>> next)
        {
            await Record($"WrapStep:before {step.Id} {(step.Memoized ? "memoized" : "new")}");
            var output = await next();
            await Record($"WrapStep:after {output ?? "null"}");
            return output;
        }

        public override async Task<object?> WrapStepHandlerAsync(FunctionContext context, StepInfo step, Func<Task<object?>> next)
        {
            await Record("WrapStepHandler:before");
            var output = await next();
            await Record("WrapStepHandler:after");
            return output;
        }

        private ValueTask Record(string hook)
        {
            lock (log)
            {
                log.Add($"{name}.{hook}");
            }

            return ValueTask.CompletedTask;
        }
    }

    private sealed class CountingMiddleware : InngestMiddleware
    {
        public CountingMiddleware() => Interlocked.Increment(ref Created);

        public static int Created;
    }

    private sealed class ThrowingObserver : InngestMiddleware
    {
        public override ValueTask OnRunStartAsync(FunctionContext context) => throw new InvalidOperationException("observer");

        public override async ValueTask OnMemoizationEndAsync(FunctionContext context)
        {
            await Task.Yield();
            throw new InvalidOperationException("observer");
        }

        public override ValueTask OnStepStartAsync(FunctionContext context, StepInfo step) => throw new InvalidOperationException("observer");

        public override ValueTask OnStepCompleteAsync(FunctionContext context, StepInfo step, object? output) =>
            throw new InvalidOperationException("observer");
    }

    private sealed class StepInObserver : InngestMiddleware
    {
        public override async ValueTask OnRunStartAsync(FunctionContext context) =>
            await context.Step.RunAsync("from-observer", () => 1);
    }

    private sealed class OutputMiddleware : InngestMiddleware
    {
        public override async Task<object?> WrapFunctionAsync(FunctionContext context, Func<Task<object?>> next)
        {
            try
            {
                return (await next() as string)?.ToUpperInvariant();
            }
            catch (InvalidOperationException exception)
            {
                throw new NonRetriableException("wrapped", exception);
            }
        }
    }

    // Encrypts step output on its way to Inngest and decrypts it on its way back to the function.
    private sealed class EncryptionMiddleware : InngestMiddleware
    {
        public override async Task<object?> WrapStepHandlerAsync(FunctionContext context, StepInfo step, Func<Task<object?>> next) =>
            await next() is string text ? "encrypted:" + text : null;

        public override async Task<object?> WrapStepAsync(FunctionContext context, StepInfo step, Func<Task<object?>> next)
        {
            try
            {
                return await next() switch
                {
                    string text => text.Replace("encrypted:", string.Empty, StringComparison.Ordinal),
                    int => "not an int",
                    var other => other,
                };
            }
            catch (StepFailedException failure)
            {
                return $"recovered from {failure.Message}";
            }
        }
    }

    // Runs an audit step before every step it wraps.
    private sealed class AuditingMiddleware(List<string> audited) : InngestMiddleware
    {
        public override async Task<object?> WrapStepAsync(FunctionContext context, StepInfo step, Func<Task<object?>> next)
        {
            lock (audited)
            {
                audited.Add(step.Id);
            }

            await context.Step.RunAsync("audit-" + step.Id, () => "audited");
            return await next();
        }
    }

    private sealed class CachingMiddleware : InngestMiddleware
    {
        public override Task<object?> WrapStepAsync(FunctionContext context, StepInfo step, Func<Task<object?>> next) =>
            Task.FromResult<object?>("from cache");
    }
}
