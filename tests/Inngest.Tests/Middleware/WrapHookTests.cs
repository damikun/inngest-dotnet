using Inngest.Tests.Execution;
using Microsoft.Extensions.DependencyInjection;

namespace Inngest.Tests.Middleware;

// The wrapping hooks as the TypeScript SDK's integration tests exercise them: how wrappers nest, what a wrapper that
// throws or catches does to the run, steps run around the function, and state that lives for one call.
public sealed class WrapHookTests
{
    private readonly List<string> _log = [];

    [Fact]
    public async Task A_wrapper_can_run_steps_before_and_after_the_function_across_calls()
    {
        using var host = new EngineHost(services => services.AddSingleton<InngestMiddleware>(new Bookends()));
        var function = TestFunction.Define(async context => await context.Step.RunAsync("main", () => "main output"));

        var first = await host.CallAsync(function, Call.New());
        var second = await host.CallAsync(function, Call.New().Data("before", "\"b\""));
        var third = await host.CallAsync(function, Call.New().Data("before", "\"b\"").Data("main", "\"main output\""));
        var last = await host.CallAsync(function, Call.New().Data("before", "\"b\"").Data("main", "\"main output\"").Data("after", "\"a\""));

        first.DisplayNames.ShouldBe(["before"]);
        second.DisplayNames.ShouldBe(["main"]);
        third.DisplayNames.ShouldBe(["after"]);
        last.StatusCode.ShouldBe(200);
        last.Body.ShouldBe("\"main output\"");
    }

    [Fact]
    public async Task Errors_pass_through_the_wrappers_from_the_inside_out()
    {
        using var host = new EngineHost(services =>
        {
            services.AddSingleton<InngestMiddleware>(new Wrapping("outer", _log));
            services.AddSingleton<InngestMiddleware>(new Wrapping("inner", _log));
        });

        var result = await host.CallAsync(TestFunction.Define(_ => throw new InvalidOperationException("fn")), Call.New());

        _log.ShouldBe(["outer:before", "inner:before", "inner:caught fn", "outer:caught inner(fn)"]);
        result.Json.GetProperty("message").GetString().ShouldBe("outer(inner(fn))");
    }

    [Fact]
    public async Task Code_after_next_is_skipped_when_the_function_throws_and_the_catch_when_it_succeeds()
    {
        using var host = new EngineHost(services => services.AddSingleton<InngestMiddleware>(new Wrapping("only", _log)));

        await host.CallAsync(TestFunction.Define(_ => Task.FromResult<object?>("fine")), Call.New());
        _log.ShouldBe(["only:before", "only:after fine"]);

        _log.Clear();
        await host.CallAsync(TestFunction.Define(_ => throw new InvalidOperationException("fn")), Call.New());
        _log.ShouldBe(["only:before", "only:caught fn"]);
    }

    [Fact]
    public async Task A_wrapper_that_throws_before_the_function_fails_the_run_without_running_it()
    {
        var ran = false;
        using var host = new EngineHost(services => services.AddSingleton<InngestMiddleware>(new Refusing()));
        var function = TestFunction.Define(_ =>
        {
            ran = true;
            return Task.FromResult<object?>(null);
        });

        var result = await host.CallAsync(function, Call.New());

        ran.ShouldBeFalse();
        result.StatusCode.ShouldBe(500);
        result.Json.GetProperty("message").GetString().ShouldBe("not allowed");
    }

    [Fact]
    public async Task A_wrapper_that_throws_after_the_function_replaces_its_result()
    {
        using var host = new EngineHost(services => services.AddSingleton<InngestMiddleware>(new FailingAfter()));

        var result = await host.CallAsync(TestFunction.Define(_ => Task.FromResult<object?>("fine")), Call.New());

        result.StatusCode.ShouldBe(400);
        result.Json.GetProperty("message").GetString().ShouldBe("rejected afterwards");
    }

    [Fact]
    public async Task A_value_set_in_the_wrapper_with_async_local_reaches_the_function_before_and_after_its_steps()
    {
        using var host = new EngineHost(services => services.AddSingleton<InngestMiddleware>(new Ambient()));
        var function = TestFunction.Define(async context =>
        {
            var before = Ambient.Value.Value;
            await context.Step.RunAsync("a", () => 1);
            return $"{before}/{Ambient.Value.Value}";
        });

        var result = await host.CallAsync(function, Call.New().Data("a", "1"));

        result.Body.ShouldBe("\"per-call/per-call\"");
    }

    [Fact]
    public async Task A_value_set_around_a_step_handler_with_async_local_reaches_the_handler()
    {
        using var host = new EngineHost(services => services.AddSingleton<InngestMiddleware>(new AmbientForHandlers()));
        var function = TestFunction.Define(async context => await context.Step.RunAsync("a", () => AmbientForHandlers.Value.Value));

        var result = await host.CallAsync(function, Call.New());

        result.Op().GetProperty("data").GetString().ShouldBe("per-handler");
    }

    [Fact]
    public async Task Middleware_state_does_not_carry_over_from_one_call_to_the_next()
    {
        using var host = new EngineHost(services => services.AddTransient<InngestMiddleware, Counting>());
        var function = TestFunction.Define(_ => Task.FromResult<object?>(null));
        Counting.Seen.Clear();

        await host.CallAsync(function, Call.New());
        await host.CallAsync(function, Call.New());
        await host.CallAsync(function, Call.New());

        Counting.Seen.ShouldBe([1, 1, 1]);
    }

    [Fact]
    public async Task One_instance_keeps_its_state_for_the_whole_call_including_replayed_steps()
    {
        using var host = new EngineHost(services => services.AddTransient<InngestMiddleware, Counting>());
        var function = TestFunction.Define(async context =>
        {
            await context.Step.RunAsync("a", () => 1);
            await context.Step.RunAsync("b", () => 2);
            return null;
        });
        Counting.Seen.Clear();

        await host.CallAsync(function, Call.New().Data("a", "1").Data("b", "2"));

        // One OnRunStart is not reported for a call with memoized steps, but the wrap hooks share the one instance.
        Counting.Seen.ShouldBe([2]);
    }

    private sealed class Bookends : InngestMiddleware
    {
        public override async Task<object?> WrapFunctionAsync(FunctionContext context, Func<Task<object?>> next)
        {
            await context.Step.RunAsync("before", () => "b");
            var output = await next();
            await context.Step.RunAsync("after", () => "a");
            return output;
        }
    }

    private sealed class Wrapping(string name, List<string> log) : InngestMiddleware
    {
        public override async Task<object?> WrapFunctionAsync(FunctionContext context, Func<Task<object?>> next)
        {
            Add($"{name}:before");
            try
            {
                var output = await next();
                Add($"{name}:after {output}");
                return output;
            }
            catch (InvalidOperationException exception)
            {
                Add($"{name}:caught {exception.Message}");
                throw new InvalidOperationException($"{name}({exception.Message})");
            }
        }

        private void Add(string entry)
        {
            lock (log)
            {
                log.Add(entry);
            }
        }
    }

    private sealed class Refusing : InngestMiddleware
    {
        public override Task<object?> WrapFunctionAsync(FunctionContext context, Func<Task<object?>> next) =>
            throw new InvalidOperationException("not allowed");
    }

    private sealed class FailingAfter : InngestMiddleware
    {
        public override async Task<object?> WrapFunctionAsync(FunctionContext context, Func<Task<object?>> next)
        {
            await next();
            throw new NonRetriableException("rejected afterwards");
        }
    }

    private sealed class Ambient : InngestMiddleware
    {
        public static readonly AsyncLocal<string?> Value = new();

        public override async Task<object?> WrapFunctionAsync(FunctionContext context, Func<Task<object?>> next)
        {
            Value.Value = "per-call";
            try
            {
                return await next();
            }
            finally
            {
                Value.Value = null;
            }
        }
    }

    private sealed class AmbientForHandlers : InngestMiddleware
    {
        public static readonly AsyncLocal<string?> Value = new();

        public override async Task<object?> WrapStepHandlerAsync(FunctionContext context, StepInfo step, Func<Task<object?>> next)
        {
            Value.Value = "per-handler";
            try
            {
                return await next();
            }
            finally
            {
                Value.Value = null;
            }
        }
    }

    private sealed class Counting : InngestMiddleware
    {
        public static readonly List<int> Seen = [];

        private int _hooks;

        public override ValueTask OnRunStartAsync(FunctionContext context)
        {
            _hooks++;
            return ValueTask.CompletedTask;
        }

        public override Task<object?> WrapStepAsync(FunctionContext context, StepInfo step, Func<Task<object?>> next)
        {
            _hooks++;
            return next();
        }

        public override async Task<object?> WrapFunctionAsync(FunctionContext context, Func<Task<object?>> next)
        {
            var output = await next();
            lock (Seen)
            {
                Seen.Add(_hooks);
            }

            return output;
        }
    }
}
