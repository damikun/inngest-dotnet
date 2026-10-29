namespace Inngest.Benchmarks.Support;

// The step ids a benchmark function works through, injected like any other dependency. The functions are classes
// resolved per call, as apps write them, with typed event data, records as step results and a record as the output.
internal sealed record StepPlan(string[] Ids)
{
    public const string SleepId = "wait-for-delivery";

    public static readonly TimeSpan SleepDuration = TimeSpan.FromDays(2);

    public static StepPlan Of(int count) => new(Samples.StepIds(count));
}

// A finished task, so that the handler's own cost stays out of the numbers.
internal static class StepHandler
{
    private static readonly Task<StepResult> Finished = Task.FromResult(Samples.Result(0));

    public static readonly Func<CancellationToken, Task<StepResult>> Charge = static _ => Finished;
}

// Awaits each step before starting the next.
internal sealed class SequentialFunction(StepPlan plan) : InngestFunction<OrderPlaced>
{
    public override async Task<object?> ExecuteAsync(FunctionContext<OrderPlaced> context, CancellationToken cancellationToken)
    {
        var total = 0L;
        foreach (var id in plan.Ids)
        {
            total += (await context.Step.RunAsync(id, StepHandler.Charge)).AmountCents;
        }

        return new Summary(plan.Ids.Length, total);
    }
}

// Starts every step, then awaits them together.
internal sealed class ParallelFunction(StepPlan plan) : InngestFunction<OrderPlaced>
{
    public override async Task<object?> ExecuteAsync(FunctionContext<OrderPlaced> context, CancellationToken cancellationToken)
    {
        var tasks = new Task<StepResult>[plan.Ids.Length];
        for (var i = 0; i < tasks.Length; i++)
        {
            tasks[i] = context.Step.RunAsync(plan.Ids[i], StepHandler.Charge);
        }

        var results = await Task.WhenAll(tasks);
        return new Summary(results.Length, results.Sum(static result => result.AmountCents));
    }
}

// Replays its steps and then sleeps, which Inngest has to be told about because the SDK cannot run it.
internal sealed class SequentialThenSleepFunction(StepPlan plan) : InngestFunction<OrderPlaced>
{
    public override async Task<object?> ExecuteAsync(FunctionContext<OrderPlaced> context, CancellationToken cancellationToken)
    {
        var total = 0L;
        foreach (var id in plan.Ids)
        {
            total += (await context.Step.RunAsync(id, StepHandler.Charge)).AmountCents;
        }

        await context.Step.SleepAsync(StepPlan.SleepId, StepPlan.SleepDuration);
        return new Summary(plan.Ids.Length, total);
    }
}

internal sealed class SingleStepFunction(StepPlan plan) : InngestFunction<OrderPlaced>
{
    public override async Task<object?> ExecuteAsync(FunctionContext<OrderPlaced> context, CancellationToken cancellationToken)
    {
        var result = await context.Step.RunAsync(plan.Ids[0], StepHandler.Charge);
        return new Summary(1, result.AmountCents);
    }
}
