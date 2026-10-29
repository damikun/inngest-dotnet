using Inngest.IntegrationTests.Harness;

namespace Inngest.IntegrationTests.Functions;

internal sealed class InvokeParentFunction : InngestFunction<NumberEvent>
{
    public static FunctionOptions Options { get; } = new()
    {
        Id = "invoke-parent",
        Triggers = [Trigger.Event("itests/invoke.parent")],
    };

    public override async Task<object?> ExecuteAsync(FunctionContext<NumberEvent> context, CancellationToken cancellationToken)
    {
        var child = await context.Step.InvokeAsync<NumberResult>(
            "double-it",
            new InvokeOptions(InvokeChildFunction.Options.Id)
            {
                Data = context.Event.Data,
                Timeout = TimeSpan.FromMinutes(1),
            });

        return new ParentResult(child);
    }
}

// Only ever invoked. It still has an event trigger, which nothing sends.
internal sealed class InvokeChildFunction(Probe probe) : InngestFunction<NumberEvent>
{
    public static FunctionOptions Options { get; } = new()
    {
        Id = "invoke-child",
        Triggers = [Trigger.Event("itests/invoke.child")],
    };

    public override Task<object?> ExecuteAsync(FunctionContext<NumberEvent> context, CancellationToken cancellationToken)
    {
        probe.For(context.Event.Data.TestId).Record("child", context.Event.Data.Value);
        return Task.FromResult<object?>(new NumberResult(context.Event.Data.Value * 2));
    }
}

// Invokes a function that fails for good and turns the resulting StepFailedException into its output.
internal sealed class InvokeCatchesFailureFunction : InngestFunction<TestEvent>
{
    public const string StepId = "call-failing-child";

    public static FunctionOptions Options { get; } = new()
    {
        Id = "invoke-catches-failure",
        Triggers = [Trigger.Event("itests/invoke.catches")],
    };

    public override async Task<object?> ExecuteAsync(FunctionContext<TestEvent> context, CancellationToken cancellationToken)
    {
        try
        {
            await context.Step.InvokeAsync<NumberResult>(StepId, new InvokeOptions(FailingChildFunction.Options.Id) { Data = context.Event.Data });
            return new CaughtFailure(false, null, null);
        }
        catch (StepFailedException exception)
        {
            return new CaughtFailure(true, exception.StepId, exception.Message);
        }
    }
}

internal sealed class FailingChildFunction(Probe probe) : InngestFunction<TestEvent>
{
    public const string Message = "The child gave up";

    public static FunctionOptions Options { get; } = new()
    {
        Id = "failing-child",
        Retries = 0,
        Triggers = [Trigger.Event("itests/invoke.failing-child")],
    };

    public override Task<object?> ExecuteAsync(FunctionContext<TestEvent> context, CancellationToken cancellationToken)
    {
        probe.For(context.Event.Data.TestId).Record("child-attempt", context.Attempt);
        throw new NonRetriableException(Message);
    }
}
