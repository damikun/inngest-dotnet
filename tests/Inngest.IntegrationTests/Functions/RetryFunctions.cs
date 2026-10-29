using Inngest.IntegrationTests.Harness;

namespace Inngest.IntegrationTests.Functions;

// Fails its first attempt outside of any step, so the whole call is retried.
internal sealed class FunctionRetryFunction(Probe probe) : InngestFunction<TestEvent>
{
    public static FunctionOptions Options { get; } = new()
    {
        Id = "retry-function",
        Retries = 2,
        Triggers = [Trigger.Event("itests/retry.function")],
    };

    public override Task<object?> ExecuteAsync(FunctionContext<TestEvent> context, CancellationToken cancellationToken)
    {
        probe.For(context.Event.Data.TestId).Record("attempt", context.Attempt);
        if (context.Attempt == 0)
        {
            throw new InvalidOperationException("The first attempt fails");
        }

        return Task.FromResult<object?>(new AttemptResult(context.Attempt));
    }
}

// A step that throws on its first two executions and succeeds on the third.
internal sealed class StepRetryFunction(Probe probe) : InngestFunction<TestEvent>
{
    public static FunctionOptions Options { get; } = new()
    {
        Id = "retry-step",
        Retries = 3,
        Triggers = [Trigger.Event("itests/retry.step")],
    };

    public override async Task<object?> ExecuteAsync(FunctionContext<TestEvent> context, CancellationToken cancellationToken)
    {
        var observed = probe.For(context.Event.Data.TestId);
        var text = await context.Step.RunAsync("flaky", () =>
        {
            if (observed.Record("flaky", context.Attempt) <= 2)
            {
                throw new InvalidOperationException("The flaky step failed");
            }

            return "recovered";
        });

        return new TextResult(text);
    }
}

internal sealed class NonRetriableFunction(Probe probe) : InngestFunction<TestEvent>
{
    public const string Message = "Bad input, retrying will not help";

    public static FunctionOptions Options { get; } = new()
    {
        Id = "non-retriable",
        Retries = 3,
        Triggers = [Trigger.Event("itests/non.retriable")],
    };

    public override Task<object?> ExecuteAsync(FunctionContext<TestEvent> context, CancellationToken cancellationToken)
    {
        probe.For(context.Event.Data.TestId).Record("attempt", context.Attempt);
        throw new NonRetriableException(Message);
    }
}

// Asks for a retry after three seconds, which the server must prefer over its one-second retry interval.
internal sealed class RetryAfterFunction(Probe probe) : InngestFunction<TestEvent>
{
    public static readonly TimeSpan Delay = TimeSpan.FromSeconds(3);

    public static FunctionOptions Options { get; } = new()
    {
        Id = "retry-after",
        Retries = 1,
        Triggers = [Trigger.Event("itests/retry.after")],
    };

    public override Task<object?> ExecuteAsync(FunctionContext<TestEvent> context, CancellationToken cancellationToken)
    {
        probe.For(context.Event.Data.TestId).Record("attempt", context.Attempt);
        if (context.Attempt == 0)
        {
            throw new RetryAfterException("Not yet", Delay);
        }

        return Task.FromResult<object?>(new AttemptResult(context.Attempt));
    }
}
