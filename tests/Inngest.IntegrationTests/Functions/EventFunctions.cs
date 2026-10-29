using Inngest.IntegrationTests.Harness;

namespace Inngest.IntegrationTests.Functions;

// Waits for itests/wait.resume from the same test. The timeout comes from the event, so one function covers both the
// matched and the timed-out case.
internal sealed class WaitForEventFunction : InngestFunction<WaitStartEvent>
{
    public const string ResumeEventName = "itests/wait.resume";

    public static FunctionOptions Options { get; } = new()
    {
        Id = "wait-for-event",
        Triggers = [Trigger.Event("itests/wait.start")],
    };

    public override async Task<object?> ExecuteAsync(FunctionContext<WaitStartEvent> context, CancellationToken cancellationToken)
    {
        var resumed = await context.Step.WaitForEventAsync<ResumeEvent>(
            "wait-for-resume",
            new WaitForEventOptions(ResumeEventName, TimeSpan.FromSeconds(context.Event.Data.TimeoutSeconds)) { Match = "data.testId" });

        return new WaitResult(resumed is not null, resumed?.Name, resumed?.Data.Message);
    }
}

// Sends an event from a step; SentEventReceiverFunction is triggered by it.
internal sealed class SendEventFunction : InngestFunction<NumberEvent>
{
    public static FunctionOptions Options { get; } = new()
    {
        Id = "send-event",
        Triggers = [Trigger.Event("itests/send.parent")],
    };

    public override async Task<object?> ExecuteAsync(FunctionContext<NumberEvent> context, CancellationToken cancellationToken)
    {
        var data = context.Event.Data;
        var sent = await context.Step.SendEventAsync(
            "notify",
            new InngestEvent("itests/send.child", new NumberEvent(data.TestId, data.Value + 1)));

        return new SentResult(sent.Ids);
    }
}

internal sealed class SentEventReceiverFunction(Probe probe) : InngestFunction<NumberEvent>
{
    public static FunctionOptions Options { get; } = new()
    {
        Id = "send-event-receiver",
        Triggers = [Trigger.Event("itests/send.child")],
    };

    public override Task<object?> ExecuteAsync(FunctionContext<NumberEvent> context, CancellationToken cancellationToken)
    {
        probe.For(context.Event.Data.TestId).Record("received", context.Event.Data.Value);
        return Task.FromResult<object?>(new NumberResult(context.Event.Data.Value));
    }
}

// Sleeps for minutes unless itests/cancel.stop arrives with the same test id.
internal sealed class CancellableFunction(Probe probe) : InngestFunction<TestEvent>
{
    public static FunctionOptions Options { get; } = new()
    {
        Id = "cancellable",
        Triggers = [Trigger.Event("itests/cancel.start")],
        CancelOn = [new CancelOn("itests/cancel.stop") { Match = "data.testId" }],
    };

    public override async Task<object?> ExecuteAsync(FunctionContext<TestEvent> context, CancellationToken cancellationToken)
    {
        var observed = probe.For(context.Event.Data.TestId);
        await context.Step.RunAsync("started", () => observed.Record("started"));
        await context.Step.SleepAsync("long-nap", TimeSpan.FromMinutes(5));
        await context.Step.RunAsync("finished", () => observed.Record("finished"));
        return null;
    }
}
