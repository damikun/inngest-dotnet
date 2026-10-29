using Inngest.IntegrationTests.Harness;

namespace Inngest.IntegrationTests.Functions;

// Three steps in a row, each using the result of the one before.
internal sealed class StepSequenceFunction(Probe probe) : InngestFunction<NumberEvent>
{
    public static FunctionOptions Options { get; } = new()
    {
        Id = "step-sequence",
        Triggers = [Trigger.Event("itests/step.sequence")],
    };

    public override async Task<object?> ExecuteAsync(FunctionContext<NumberEvent> context, CancellationToken cancellationToken)
    {
        var observed = probe.For(context.Event.Data.TestId);
        var first = await context.Step.RunAsync("first", () =>
        {
            observed.Record("first");
            return context.Event.Data.Value + 1;
        });
        var second = await context.Step.RunAsync("second", () =>
        {
            observed.Record("second");
            return first * 10;
        });
        var third = await context.Step.RunAsync("third", () =>
        {
            observed.Record("third");
            return $"{first}:{second}";
        });

        return new SequenceResult(first, second, third);
    }
}

// Three steps started together and awaited with Task.WhenAll, then one step that combines their results.
internal sealed class ParallelFunction(Probe probe) : InngestFunction<NumberEvent>
{
    public static FunctionOptions Options { get; } = new()
    {
        Id = "parallel",
        Triggers = [Trigger.Event("itests/parallel")],
    };

    public override async Task<object?> ExecuteAsync(FunctionContext<NumberEvent> context, CancellationToken cancellationToken)
    {
        var observed = probe.For(context.Event.Data.TestId);
        var value = context.Event.Data.Value;
        var parts = await Task.WhenAll(Enumerable.Range(1, 3).Select(index => context.Step.RunAsync($"part-{index}", () =>
        {
            observed.Record($"part-{index}");
            return index * value;
        })));
        var total = await context.Step.RunAsync("total", () =>
        {
            observed.Record("total");
            return parts.Sum();
        });

        return new ParallelResult(parts, total);
    }
}
