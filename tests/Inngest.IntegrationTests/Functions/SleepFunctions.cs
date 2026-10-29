using Inngest.IntegrationTests.Harness;

namespace Inngest.IntegrationTests.Functions;

internal sealed class SleepFunction(Probe probe) : InngestFunction<TestEvent>
{
    public static readonly TimeSpan Duration = TimeSpan.FromSeconds(1);

    public static FunctionOptions Options { get; } = new()
    {
        Id = "sleep",
        Triggers = [Trigger.Event("itests/sleep")],
    };

    public override async Task<object?> ExecuteAsync(FunctionContext<TestEvent> context, CancellationToken cancellationToken)
    {
        var observed = probe.For(context.Event.Data.TestId);
        await context.Step.RunAsync("before-nap", () => observed.Record("before-nap"));
        await context.Step.SleepAsync("nap", Duration);
        await context.Step.RunAsync("after-nap", () => observed.Record("after-nap"));
        return null;
    }
}

// The wake-up time comes from the event, so it is the same on every replay.
internal sealed class SleepUntilFunction(Probe probe) : InngestFunction<SleepUntilEvent>
{
    public static FunctionOptions Options { get; } = new()
    {
        Id = "sleep-until",
        Triggers = [Trigger.Event("itests/sleep.until")],
    };

    public override async Task<object?> ExecuteAsync(FunctionContext<SleepUntilEvent> context, CancellationToken cancellationToken)
    {
        var observed = probe.For(context.Event.Data.TestId);
        await context.Step.SleepUntilAsync("until", context.Event.Data.Until);
        await context.Step.RunAsync("woke", () => observed.Record("woke"));
        return null;
    }
}
