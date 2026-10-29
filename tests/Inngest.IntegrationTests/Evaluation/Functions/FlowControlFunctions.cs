using Inngest.IntegrationTests.Evaluation.Harness;
using Inngest.IntegrationTests.Harness;

namespace Inngest.IntegrationTests.Evaluation.Functions;

internal sealed record GroupEvent(string TestId, string Group, int Value = 0) : IScenarioEvent;

internal sealed record RoleEvent(string TestId, string Role, int Priority = 0, int Seconds = 0) : IScenarioEvent;

internal sealed record OrderEvent(string TestId, string OrderId, int Seconds = 0) : IScenarioEvent;

internal sealed record StopEvent(string TestId, string Reason = "stop", string? OrderId = null);

internal sealed record GroupInterval(string Group, Interval Interval);

// Every limit is keyed by the scenario's test id, so that parallel scenarios and the two runs of a scenario's events
// never share a queue.
internal static class ConcurrencyFunctions
{
    public const string Limit = "eval/fc.limit";
    public const string Keyed = "eval/fc.keyed";
    public const string Steps = "eval/fc.steps";
    public const string Sleeper = "eval/fc.sleeper";
    public const string Prioritized = "eval/fc.priority";

    public static readonly TimeSpan Work = TimeSpan.FromMilliseconds(800);

    public static void Add(IInngestBuilder inngest)
    {
        inngest.AddFunction<ValueEvent>(
            new FunctionOptions
            {
                Id = "fc-limit",
                Triggers = [Trigger.Event(Limit)],
                Concurrency = [new ConcurrencyLimit(2) { Key = "event.data.testId" }],
            },
            static async (context, _) =>
            {
                var observed = context.Observed();
                await context.Step.RunAsync("work", cancellationToken => TimedAsync(observed, "work", Work, cancellationToken));
                return null;
            });

        inngest.AddFunction<GroupEvent>(
            new FunctionOptions
            {
                Id = "fc-keyed",
                Triggers = [Trigger.Event(Keyed)],
                Concurrency = [new ConcurrencyLimit(1) { Key = "event.data.testId + '-' + event.data.group" }],
            },
            static async (context, _) =>
            {
                var observed = context.Observed();
                var group = context.Event.Data.Group;
                await context.Step.RunAsync("work", async cancellationToken =>
                {
                    var start = DateTimeOffset.UtcNow;
                    await Task.Delay(Work, cancellationToken);
                    observed.Record("work", new GroupInterval(group, new Interval(start, DateTimeOffset.UtcNow)));
                });
                return null;
            });

        // The limit applies to steps, so it also keeps the parallel steps of one run apart.
        inngest.AddFunction<RunEvent>(
            new FunctionOptions
            {
                Id = "fc-steps",
                Triggers = [Trigger.Event(Steps)],
                Concurrency = [new ConcurrencyLimit(1) { Key = "event.data.testId" }],
            },
            static async (context, _) =>
            {
                var observed = context.Observed();
                await Task.WhenAll(Enumerable.Range(1, 3).Select(part =>
                    context.Step.RunAsync($"part-{part}", cancellationToken => TimedAsync(observed, "part", TimeSpan.FromMilliseconds(500), cancellationToken))));
                return null;
            });

        // The "sleeper" sleeps between two steps; the "quick" run has a single step.
        inngest.AddFunction<RoleEvent>(
            new FunctionOptions
            {
                Id = "fc-sleeper",
                Triggers = [Trigger.Event(Sleeper)],
                Concurrency = [new ConcurrencyLimit(1) { Key = "event.data.testId" }],
            },
            static async (context, _) =>
            {
                var observed = context.Observed();
                if (context.Event.Data.Role == "quick")
                {
                    await context.Step.RunAsync("quick", () => observed.Record("quick"));
                    return null;
                }

                await context.Step.RunAsync("before-sleep", () => observed.Record("before-sleep"));
                await context.Step.SleepAsync("sleep", TimeSpan.FromSeconds(context.Event.Data.Seconds));
                await context.Step.RunAsync("after-sleep", () => observed.Record("after-sleep"));
                return null;
            });

        // One run holds the only slot while the others queue up behind it. The expression must give an int: a JSON
        // number in event data is a double to the server's expression engine, which then ignores the priority.
        inngest.AddFunction<RoleEvent>(
            new FunctionOptions
            {
                Id = "fc-priority",
                Triggers = [Trigger.Event(Prioritized)],
                Concurrency = [new ConcurrencyLimit(1) { Key = "event.data.testId" }],
                Priority = new Priority("event.data.role == 'high' ? 60 : 0"),
            },
            static async (context, _) =>
            {
                var observed = context.Observed();
                var role = context.Event.Data.Role;
                await context.Step.RunAsync("work", async cancellationToken =>
                {
                    observed.Record("started", role);
                    await Task.Delay(TimeSpan.FromSeconds(context.Event.Data.Seconds), cancellationToken);
                });
                return null;
            });
    }

    public static async Task<Interval> TimedAsync(TestRecording observed, string name, TimeSpan work, CancellationToken cancellationToken)
    {
        var start = DateTimeOffset.UtcNow;
        await Task.Delay(work, cancellationToken);
        var interval = new Interval(start, DateTimeOffset.UtcNow);
        observed.Record(name, interval);
        return interval;
    }
}

internal static class RateFunctions
{
    public const string Throttled = "eval/fc.throttle";
    public const string RateLimited = "eval/fc.rate";
    public const string Idempotent = "eval/fc.idempotent";

    public static readonly TimeSpan ThrottlePeriod = TimeSpan.FromSeconds(2);

    public static void Add(IInngestBuilder inngest)
    {
        inngest.AddFunction<ValueEvent>(
            new FunctionOptions
            {
                Id = "fc-throttle",
                Triggers = [Trigger.Event(Throttled)],
                Throttle = new Throttle(1, ThrottlePeriod) { Key = "event.data.testId" },
            },
            static (context, _) =>
            {
                context.Observed().Record("started", context.Event.Data.Value);
                return Task.FromResult<object?>(context.Event.Data.Value);
            });

        inngest.AddFunction<ValueEvent>(
            new FunctionOptions
            {
                Id = "fc-rate",
                Triggers = [Trigger.Event(RateLimited)],
                RateLimit = new RateLimit(1, TimeSpan.FromMinutes(1)) { Key = "event.data.testId" },
            },
            static (context, _) =>
            {
                context.Observed().Record("ran", context.Event.Data.Value);
                return Task.FromResult<object?>(context.Event.Data.Value);
            });

        inngest.AddFunction<OrderEvent>(
            new FunctionOptions
            {
                Id = "fc-idempotent",
                Triggers = [Trigger.Event(Idempotent)],
                Idempotency = "event.data.testId + '-' + event.data.orderId",
            },
            static (context, _) =>
            {
                context.Observed().Record("ran", context.Event.Data.OrderId);
                return Task.FromResult<object?>(context.Event.Data.OrderId);
            });
    }
}

internal static class DebounceAndBatchFunctions
{
    public const string Debounced = "eval/fc.debounce";
    public const string DebouncedWithTimeout = "eval/fc.debounce-timeout";
    public const string Batched = "eval/fc.batch";
    public const string BatchedWithTimeout = "eval/fc.batch-timeout";

    public static readonly TimeSpan DebouncePeriod = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan DebounceTimeout = TimeSpan.FromSeconds(4);
    public static readonly TimeSpan BatchTimeout = TimeSpan.FromSeconds(2);

    public static void Add(IInngestBuilder inngest)
    {
        inngest.AddFunction<ValueEvent>(
            new FunctionOptions
            {
                Id = "fc-debounce",
                Triggers = [Trigger.Event(Debounced)],
                Debounce = new Debounce(DebouncePeriod) { Key = "event.data.testId" },
            },
            static (context, _) =>
            {
                context.Observed().Record("ran", context.Event.Data.Value);
                return Task.FromResult<object?>(context.Event.Data.Value);
            });

        inngest.AddFunction<ValueEvent>(
            new FunctionOptions
            {
                Id = "fc-debounce-timeout",
                Triggers = [Trigger.Event(DebouncedWithTimeout)],
                Debounce = new Debounce(DebouncePeriod) { Key = "event.data.testId", Timeout = DebounceTimeout },
            },
            static (context, _) =>
            {
                context.Observed().Record("ran", context.Event.Data.Value);
                return Task.FromResult<object?>(context.Event.Data.Value);
            });

        inngest.AddFunction<ValueEvent>(
            new FunctionOptions
            {
                Id = "fc-batch",
                Triggers = [Trigger.Event(Batched)],
                BatchEvents = new BatchEvents(3, TimeSpan.FromSeconds(30)) { Key = "event.data.testId" },
            },
            static (context, _) =>
            {
                context.Observed().Record("batch", context.Events.Select(@event => @event.Data.Value).ToArray());
                return Task.FromResult<object?>(context.Events.Count);
            });

        inngest.AddFunction<ValueEvent>(
            new FunctionOptions
            {
                Id = "fc-batch-timeout",
                Triggers = [Trigger.Event(BatchedWithTimeout)],
                BatchEvents = new BatchEvents(10, BatchTimeout) { Key = "event.data.testId" },
            },
            static (context, _) =>
            {
                context.Observed().Record("batch", context.Events.Select(@event => @event.Data.Value).ToArray());
                return Task.FromResult<object?>(context.Events.Count);
            });
    }
}

internal static class SingletonAndTimeoutFunctions
{
    public const string Skipping = "eval/fc.singleton-skip";
    public const string Cancelling = "eval/fc.singleton-cancel";
    public const string StartTimeout = "eval/fc.start-timeout";
    public const string FinishTimeout = "eval/fc.finish-timeout";

    public static readonly TimeSpan StartLimit = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan FinishLimit = TimeSpan.FromSeconds(2);

    public static void Add(IInngestBuilder inngest)
    {
        inngest.AddFunction<OrderEvent>(
            new FunctionOptions
            {
                Id = "fc-singleton-skip",
                Triggers = [Trigger.Event(Skipping)],
                Singleton = new Singleton(SingletonMode.Skip) { Key = "event.data.testId" },
            },
            static (context, _) => SleepBetweenStepsAsync(context));

        inngest.AddFunction<OrderEvent>(
            new FunctionOptions
            {
                Id = "fc-singleton-cancel",
                Triggers = [Trigger.Event(Cancelling)],
                Singleton = new Singleton(SingletonMode.Cancel) { Key = "event.data.testId" },
            },
            static (context, _) => SleepBetweenStepsAsync(context));

        // The holder keeps the only slot busy longer than the others may wait in the queue.
        inngest.AddFunction<RoleEvent>(
            new FunctionOptions
            {
                Id = "fc-start-timeout",
                Triggers = [Trigger.Event(StartTimeout)],
                Concurrency = [new ConcurrencyLimit(1) { Key = "event.data.testId" }],
                Timeouts = new FunctionTimeouts { Start = StartLimit },
            },
            static async (context, _) =>
            {
                var observed = context.Observed();
                var role = context.Event.Data.Role;
                await context.Step.RunAsync("work", async cancellationToken =>
                {
                    observed.Record("started", role);
                    await Task.Delay(TimeSpan.FromSeconds(context.Event.Data.Seconds), cancellationToken);
                });
                return role;
            });

        inngest.AddFunction<OrderEvent>(
            new FunctionOptions
            {
                Id = "fc-finish-timeout",
                Triggers = [Trigger.Event(FinishTimeout)],
                Timeouts = new FunctionTimeouts { Finish = FinishLimit },
            },
            static (context, _) => SleepBetweenStepsAsync(context));
    }

    // Records the order id before and after a sleep of the event's length.
    public static async Task<object?> SleepBetweenStepsAsync(FunctionContext<OrderEvent> context)
    {
        var observed = context.Observed();
        var order = context.Event.Data.OrderId;
        await context.Step.RunAsync("start", () => observed.Record("start", order));
        await context.Step.SleepAsync("sleep", TimeSpan.FromSeconds(context.Event.Data.Seconds));
        await context.Step.RunAsync("end", () => observed.Record("end", order));
        return order;
    }
}

internal static class CancellationFunctions
{
    public const string ByExpression = "eval/fc.cancel-if";
    public const string ByExpressionStop = "eval/fc.cancel-if.stop";
    public const string ByMatch = "eval/fc.cancel-match";
    public const string ByMatchStop = "eval/fc.cancel-match.stop";
    public const string Windowed = "eval/fc.cancel-window";
    public const string WindowedStop = "eval/fc.cancel-window.stop";

    public static readonly TimeSpan Window = TimeSpan.FromSeconds(1);

    public static void Add(IInngestBuilder inngest)
    {
        inngest.AddFunction<OrderEvent>(
            new FunctionOptions
            {
                Id = "fc-cancel-if",
                Triggers = [Trigger.Event(ByExpression)],
                CancelOn = [new CancelOn(ByExpressionStop) { If = "async.data.testId == event.data.testId && async.data.reason == 'stop'" }],
            },
            static (context, _) => SingletonAndTimeoutFunctions.SleepBetweenStepsAsync(context));

        inngest.AddFunction<OrderEvent>(
            new FunctionOptions
            {
                Id = "fc-cancel-match",
                Triggers = [Trigger.Event(ByMatch)],
                CancelOn = [new CancelOn(ByMatchStop) { Match = "data.orderId" }],
            },
            static (context, _) => SingletonAndTimeoutFunctions.SleepBetweenStepsAsync(context));

        // The cancellation only listens for a second after the run is scheduled.
        inngest.AddFunction<OrderEvent>(
            new FunctionOptions
            {
                Id = "fc-cancel-window",
                Triggers = [Trigger.Event(Windowed)],
                CancelOn = [new CancelOn(WindowedStop) { Match = "data.testId", Timeout = Window }],
            },
            static (context, _) => SingletonAndTimeoutFunctions.SleepBetweenStepsAsync(context));
    }
}
