using Inngest.Functions;
using Inngest.Tests.Execution;
using Microsoft.Extensions.DependencyInjection;
using static Inngest.Tests.Checkpointing.Checkpointed;

namespace Inngest.Tests.Checkpointing;

// Middleware sees each step that a checkpointing call runs exactly as it would in a call of its own: OnStepStartAsync,
// WrapStepHandlerAsync and OnStepCompleteAsync around the handler, and WrapStepAsync gets the result the next call
// would have read from Inngest.
public sealed class CheckpointingMiddlewareTests
{
    private readonly List<string> _log = [];

    [Fact]
    public async Task Hooks_run_in_the_same_order_for_every_step_of_the_call()
    {
        using var host = Host(new Recorder(_log));

        var result = await host.CallCheckpointedAsync(TwoSteps(), Call.New());

        result.Body.ShouldBe("\"AB\"");
        _log.ShouldBe(
        [
            "OnMemoizationEnd",
            "OnRunStart",
            "WrapStep A",
            "OnStepStart A",
            "WrapStepHandler A",
            "WrapStepHandler A done",
            "OnStepComplete A A",
            "checkpoint A",
            "WrapStep A done A",
            "WrapStep B",
            "OnStepStart B",
            "WrapStepHandler B",
            "WrapStepHandler B done",
            "OnStepComplete B B",
            "checkpoint B",
            "WrapStep B done B",
            "OnRunComplete AB",
        ]);
    }

    [Fact]
    public async Task A_step_error_reaches_OnStepErrorAsync_after_the_steps_before_it_are_saved()
    {
        using var host = Host(new Recorder(_log));
        var function = Define(async context =>
        {
            await context.Step.RunAsync("A", () => "A");
            await context.Step.RunAsync<string>("B", _ => throw new InvalidOperationException("declined"));
            return null;
        });

        var result = await host.CallCheckpointedAsync(function, Call.New());

        result.OpCodes.ShouldBe(["StepError"]);
        _log.SkipWhile(entry => entry != "checkpoint A").ShouldBe(
        [
            "checkpoint A",
            "WrapStep A done A",
            "WrapStep B",
            "OnStepStart B",
            "WrapStepHandler B",
            "WrapStepHandler B done",
            "OnStepError B declined final=False",
        ]);
    }

    [Fact]
    public async Task WrapStepAsync_can_change_the_result_the_function_gets_but_not_what_is_saved()
    {
        using var host = Host(new Exclaiming());

        var result = await host.CallCheckpointedAsync(TwoSteps(), Call.New());

        result.Body.ShouldBe("\"A!B!\"");
        host.Checkpoints.Checkpoints.Select(checkpoint => checkpoint.Ops[0].GetProperty("data").GetString()).ShouldBe(["A", "B"]);
    }

    [Fact]
    public async Task A_step_reported_from_another_thread_still_gets_its_result()
    {
        using var host = Host(new Detaching(), new Recorder(_log));

        var result = await host.CallCheckpointedAsync(TwoSteps(), Call.New());

        result.Body.ShouldBe("\"AB\"");
        host.Checkpoints.Checkpoints.Count.ShouldBe(2);
    }

    private EngineHost Host(params InngestMiddleware[] middleware)
    {
        var host = new EngineHost(services =>
        {
            foreach (var instance in middleware)
            {
                services.AddSingleton(instance);
            }
        });
        host.Checkpoints.Timeline = _log;
        return host;
    }

    private static FunctionDefinition TwoSteps() => Define(async context =>
    {
        var a = await context.Step.RunAsync("A", () => "A");
        var b = await context.Step.RunAsync("B", () => "B");
        return a + b;
    });

    private sealed class Recorder(List<string> log) : InngestMiddleware
    {
        public override ValueTask OnMemoizationEndAsync(FunctionContext context) => Add("OnMemoizationEnd");

        public override ValueTask OnRunStartAsync(FunctionContext context) => Add("OnRunStart");

        public override ValueTask OnRunCompleteAsync(FunctionContext context, object? output) => Add($"OnRunComplete {output}");

        public override ValueTask OnStepStartAsync(FunctionContext context, StepInfo step) => Add($"OnStepStart {step.Id}");

        public override ValueTask OnStepCompleteAsync(FunctionContext context, StepInfo step, object? output) =>
            Add($"OnStepComplete {step.Id} {output}");

        public override ValueTask OnStepErrorAsync(FunctionContext context, StepInfo step, Exception exception, bool isFinalAttempt) =>
            Add($"OnStepError {step.Id} {exception.Message} final={isFinalAttempt}");

        public override async Task<object?> WrapStepAsync(FunctionContext context, StepInfo step, Func<Task<object?>> next)
        {
            await Add($"WrapStep {step.Id}");
            var result = await next();
            await Add($"WrapStep {step.Id} done {result}");
            return result;
        }

        public override async Task<object?> WrapStepHandlerAsync(FunctionContext context, StepInfo step, Func<Task<object?>> next)
        {
            await Add($"WrapStepHandler {step.Id}");
            try
            {
                return await next();
            }
            finally
            {
                await Add($"WrapStepHandler {step.Id} done");
            }
        }

        private ValueTask Add(string entry)
        {
            lock (log)
            {
                log.Add(entry);
            }

            return ValueTask.CompletedTask;
        }
    }

    private sealed class Exclaiming : InngestMiddleware
    {
        public override async Task<object?> WrapStepAsync(FunctionContext context, StepInfo step, Func<Task<object?>> next) =>
            (string?)await next() + "!";
    }

    // Calls next() from the thread pool, so the engine learns about the step from another thread.
    private sealed class Detaching : InngestMiddleware
    {
        public override async Task<object?> WrapStepAsync(FunctionContext context, StepInfo step, Func<Task<object?>> next)
        {
            await Task.Run(() => { }).ConfigureAwait(false);
            return await next().ConfigureAwait(false);
        }
    }
}
