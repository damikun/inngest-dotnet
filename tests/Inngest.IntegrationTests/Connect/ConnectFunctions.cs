using Inngest.IntegrationTests.Functions;
using Inngest.IntegrationTests.Harness;

namespace Inngest.IntegrationTests.Connect;

// The functions of the Connect worker app. Their events start with itests/connect so that the HTTP app on the other
// dev server never sees them.
internal static class ConnectFunctions
{
    public const string AppId = "itests-connect";

    public static IReadOnlyList<string> Slugs { get; } = ["itests-connect-steps", "itests-connect-retry"];

    public static IInngestBuilder AddConnectFunctions(this IInngestBuilder inngest)
    {
        inngest.AddFunction<ConnectStepsFunction>(ConnectStepsFunction.Options);
        inngest.AddFunction<ConnectRetryFunction>(ConnectRetryFunction.Options);
        return inngest;
    }
}

internal sealed record ConnectStepsResult(int Start, IReadOnlyList<int> Parts, int Total);

// A step, three steps in parallel, a sleep and a last step: every kind of call a run makes to its app.
internal sealed class ConnectStepsFunction(Probe probe) : InngestFunction<NumberEvent>
{
    public static readonly TimeSpan Pause = TimeSpan.FromSeconds(1);

    public static FunctionOptions Options { get; } = new()
    {
        Id = "steps",
        Triggers = [Trigger.Event("itests/connect.steps")],
    };

    public override async Task<object?> ExecuteAsync(FunctionContext<NumberEvent> context, CancellationToken cancellationToken)
    {
        var observed = probe.For(context.Event.Data.TestId);
        var start = await context.Step.RunAsync("start", () =>
        {
            observed.Record("start");
            return context.Event.Data.Value + 1;
        });
        var parts = await Task.WhenAll(Enumerable.Range(1, 3).Select(index => context.Step.RunAsync($"part-{index}", () =>
        {
            observed.Record($"part-{index}");
            return index * start;
        })));
        await context.Step.SleepAsync("pause", Pause);
        var total = await context.Step.RunAsync("total", () =>
        {
            observed.Record("total");
            return parts.Sum();
        });

        return new ConnectStepsResult(start, parts, total);
    }
}

// A step that fails on its first attempt and succeeds when Inngest retries it.
internal sealed class ConnectRetryFunction(Probe probe) : InngestFunction<TestEvent>
{
    public static FunctionOptions Options { get; } = new()
    {
        Id = "retry",
        Retries = 2,
        Triggers = [Trigger.Event("itests/connect.retry")],
    };

    public override async Task<object?> ExecuteAsync(FunctionContext<TestEvent> context, CancellationToken cancellationToken)
    {
        var observed = probe.For(context.Event.Data.TestId);
        var text = await context.Step.RunAsync("flaky", () =>
        {
            if (observed.Record("flaky", context.Attempt) == 1)
            {
                throw new InvalidOperationException("The first attempt of the step fails");
            }

            return "recovered";
        });

        return new TextResult(text);
    }
}

// Holds its run inside the function until the test lets it go, so that the worker has an execution in flight when
// the host stops. It has no steps, so the one call that is in flight is also the one that finishes the run.
internal sealed class SlowFunction(SlowFunction.Gate gate) : InngestFunction<TestEvent>
{
    public const string AppId = "itests-connect-shutdown";
    public const string Slug = "itests-connect-shutdown-slow";

    public static FunctionOptions Options { get; } = new()
    {
        Id = "slow",
        Triggers = [Trigger.Event("itests/connect.slow")],
    };

    public override async Task<object?> ExecuteAsync(FunctionContext<TestEvent> context, CancellationToken cancellationToken)
    {
        gate.Entered.TrySetResult();
        await gate.Released.Task.WaitAsync(cancellationToken);
        return new TextResult("finished");
    }

    public sealed class Gate
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Released { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
