using System.Text.Json;

namespace Inngest.IntegrationTests.Evaluation.Functions;

internal sealed record InvokeFailure(bool Caught, string? StepId, string? ErrorName, string? Message);

internal sealed record ChildView(string Name, JsonElement Data);

internal sealed record NestedPayload(string TestId, string Customer, IReadOnlyList<int> Items, IReadOnlyDictionary<string, bool> Flags) : IScenarioEvent;

internal sealed record ReceivedEvent(int Value, string? Id, DateTimeOffset Timestamp, string? Version, string Name);

internal sealed record SentIds(IReadOnlyList<string> Ids);

internal sealed record TwoSends(IReadOnlyList<string> First, IReadOnlyList<string> Second);

internal static class InvokeFunctions
{
    public const string Chain = "eval/inv.chain";
    public const string Catch = "eval/inv.catch";
    public const string Uncaught = "eval/inv.uncaught";
    public const string Timeout = "eval/inv.timeout";
    public const string Parallel = "eval/inv.parallel";
    public const string Data = "eval/inv.data";
    public const string Missing = "eval/inv.missing";
    public const string Recovering = "eval/inv.recovering";

    public const string ChildFailure = "the child exploded";
    public const string ChildRefusal = "the child refuses";

    public static readonly TimeSpan ChildSleep = TimeSpan.FromSeconds(4);

    public static void Add(IInngestBuilder inngest)
    {
        // A invokes B, which invokes C; nothing triggers B or C but an invoke.
        inngest.AddFunction<ValueEvent>(
            new FunctionOptions { Id = "inv-chain-a", Triggers = [Trigger.Event(Chain)] },
            static async (context, _) =>
                await context.Step.InvokeAsync<int>("call-b", new InvokeOptions("inv-chain-b") { Data = context.Event.Data }) + 10);

        inngest.AddFunction<ValueEvent>(
            new FunctionOptions { Id = "inv-chain-b", Triggers = [] },
            static async (context, _) =>
            {
                context.Observed().Record("b");
                return await context.Step.InvokeAsync<int>("call-c", new InvokeOptions("inv-chain-c") { Data = context.Event.Data }) + 1;
            });

        inngest.AddFunction<ValueEvent>(
            new FunctionOptions { Id = "inv-chain-c", Triggers = [] },
            static (context, _) =>
            {
                context.Observed().Record("c");
                return Task.FromResult<object?>(context.Event.Data.Value * 2);
            });

        inngest.AddFunction<RunEvent>(
            new FunctionOptions { Id = "inv-catch", Triggers = [Trigger.Event(Catch)] },
            static async (context, _) =>
            {
                try
                {
                    await context.Step.InvokeAsync<int>("call-flaky", new InvokeOptions("inv-failing-child") { Data = context.Event.Data });
                    return new InvokeFailure(false, null, null, null);
                }
                catch (StepFailedException exception)
                {
                    return new InvokeFailure(true, exception.StepId, exception.Error.Name, exception.Message);
                }
            });

        inngest.AddFunction<RunEvent>(
            new FunctionOptions { Id = "inv-failing-child", Retries = 1, Triggers = [] },
            static (context, _) =>
            {
                context.Observed().Record("child-attempt", context.Attempt);
                throw new InvalidOperationException(ChildFailure);
            });

        // Not caught, so the parent fails, and without being retried even though it has retries left.
        inngest.AddFunction<RunEvent>(
            new FunctionOptions { Id = "inv-uncaught", Retries = 2, Triggers = [Trigger.Event(Uncaught)] },
            static async (context, _) =>
            {
                try
                {
                    return await context.Step.InvokeAsync<int>("call-doomed", new InvokeOptions("inv-refusing-child") { Data = context.Event.Data });
                }
                catch (StepFailedException)
                {
                    // Recorded on every call that sees the failure; a retry of the parent would record it again.
                    context.Observed().Record("parent-saw-failure");
                    throw;
                }
            });

        inngest.AddFunction<RunEvent>(
            new FunctionOptions { Id = "inv-refusing-child", Retries = 3, Triggers = [] },
            static (context, _) =>
            {
                context.Observed().Record("child-attempt", context.Attempt);
                throw new NonRetriableException(ChildRefusal);
            });

        inngest.AddFunction<RunEvent>(
            new FunctionOptions { Id = "inv-timeout", Triggers = [Trigger.Event(Timeout)] },
            static async (context, _) =>
            {
                try
                {
                    await context.Step.InvokeAsync<string>(
                        "call-sleepy",
                        new InvokeOptions("inv-sleepy-child") { Data = context.Event.Data, Timeout = TimeSpan.FromSeconds(1) });
                    return new InvokeFailure(false, null, null, null);
                }
                catch (StepFailedException exception)
                {
                    context.Observed().Record("parent-gave-up");
                    return new InvokeFailure(true, exception.StepId, exception.Error.Name, exception.Message);
                }
            });

        inngest.AddFunction<RunEvent>(
            new FunctionOptions { Id = "inv-sleepy-child", Triggers = [] },
            static async (context, _) =>
            {
                await context.Step.SleepAsync("nap", ChildSleep);
                await context.Step.RunAsync("finish", () => context.Observed().Record("child-finished"));
                return "finished late";
            });

        inngest.AddFunction<RunEvent>(
            new FunctionOptions { Id = "inv-parallel", Triggers = [Trigger.Event(Parallel)] },
            static async (context, _) =>
                await Task.WhenAll(Enumerable.Range(1, 5).Select(value =>
                    context.Step.InvokeAsync<int>($"square-{value}", new InvokeOptions("inv-square") { Data = new ValueEvent(context.Event.Data.TestId, value) }))));

        inngest.AddFunction<ValueEvent>(
            new FunctionOptions { Id = "inv-square", Triggers = [] },
            static (context, _) =>
            {
                context.Observed().Record("square", context.Event.Data.Value);
                return Task.FromResult<object?>(context.Event.Data.Value * context.Event.Data.Value);
            });

        inngest.AddFunction<NestedPayload>(
            new FunctionOptions { Id = "inv-data", Triggers = [Trigger.Event(Data)] },
            static async (context, _) =>
                await context.Step.InvokeAsync<ChildView>("call-echo", new InvokeOptions("inv-echo-child") { Data = context.Event.Data }));

        inngest.AddFunction<JsonElement>(
            new FunctionOptions { Id = "inv-echo-child", Triggers = [] },
            static (context, _) => Task.FromResult<object?>(new ChildView(context.Event.Name, context.Event.Data)));

        // The timeout only bounds the scenario in case the server never answers for a function it does not know.
        inngest.AddFunction<RunEvent>(
            new FunctionOptions { Id = "inv-missing", Triggers = [Trigger.Event(Missing)] },
            static async (context, _) =>
            {
                try
                {
                    await context.Step.InvokeAsync<int>("call-nobody", new InvokeOptions("does-not-exist") { Timeout = TimeSpan.FromSeconds(20) });
                    return new InvokeFailure(false, null, null, null);
                }
                catch (StepFailedException exception)
                {
                    return new InvokeFailure(true, exception.StepId, exception.Error.Name, exception.Message);
                }
            });

        inngest.AddFunction<ValueEvent>(
            new FunctionOptions { Id = "inv-recovering", Triggers = [Trigger.Event(Recovering)] },
            static async (context, _) =>
                await context.Step.InvokeAsync<int>("call-recovering", new InvokeOptions("inv-recovering-child") { Data = context.Event.Data }));

        inngest.AddFunction<ValueEvent>(
            new FunctionOptions { Id = "inv-recovering-child", Retries = 2, Triggers = [] },
            static (context, _) =>
            {
                context.Observed().Record("child-attempt", context.Attempt);
                return context.Attempt == 0
                    ? throw new InvalidOperationException("the first attempt of the child fails")
                    : Task.FromResult<object?>(context.Event.Data.Value + 100);
            });
    }
}

internal static class SendEventFunctions
{
    public const string Many = "eval/send.many";
    public const string Once = "eval/send.once";
    public const string Received = "eval/send.received";
    public const string WaitStart = "eval/send.wait.start";
    public const string SignalStart = "eval/send.signal.start";
    public const string Signal = "eval/send.signal";
    public const string Dedupe = "eval/send.dedupe";
    public const string Described = "eval/send.described";

    public static readonly DateTimeOffset DescribedTimestamp = new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);

    public static void Add(IInngestBuilder inngest)
    {
        inngest.AddFunction<ValueEvent>(
            new FunctionOptions { Id = "send-receiver", Triggers = [Trigger.Event(Received)] },
            static (context, _) =>
            {
                var @event = context.Event;
                context.Observed().Record("received", new ReceivedEvent(@event.Data.Value, @event.Id, @event.Timestamp, @event.Version, @event.Name));
                return Task.FromResult<object?>(@event.Data.Value);
            });

        inngest.AddFunction<RunEvent>(
            new FunctionOptions { Id = "send-many", Triggers = [Trigger.Event(Many)] },
            static async (context, _) =>
            {
                var sent = await context.Step.SendEventAsync(
                    "fan-out",
                    [.. Enumerable.Range(1, 3).Select(value => new InngestEvent(Received, new ValueEvent(context.Event.Data.TestId, value)))]);
                return new SentIds(sent.Ids);
            });

        // The step after the send fails once, so the function replays past the send without sending again.
        inngest.AddFunction<RunEvent>(
            new FunctionOptions { Id = "send-once", Retries = 2, Triggers = [Trigger.Event(Once)] },
            static async (context, _) =>
            {
                var observed = context.Observed();
                var sent = await context.Step.SendEventAsync("notify", new InngestEvent(Received, new ValueEvent(context.Event.Data.TestId, 7)));
                await context.Step.RunAsync("flaky", () => observed.Record("flaky", context.Attempt) == 1
                    ? throw new InvalidOperationException("fails once")
                    : "ok");
                await context.Step.SleepAsync("pause", TimeSpan.FromSeconds(1));
                return new SentIds(sent.Ids);
            });

        inngest.AddFunction<RunEvent>(
            new FunctionOptions { Id = "send-waiter", Triggers = [Trigger.Event(WaitStart)] },
            static async (context, _) =>
            {
                var signal = await context.Step.WaitForEventAsync<ValueEvent>("signal", new WaitForEventOptions(Signal, TimeSpan.FromSeconds(30)) { Match = "data.testId" });
                return signal?.Data.Value;
            });

        inngest.AddFunction<ValueEvent>(
            new FunctionOptions { Id = "send-signaler", Triggers = [Trigger.Event(SignalStart)] },
            static async (context, _) =>
                new SentIds((await context.Step.SendEventAsync("signal", new InngestEvent(Signal, context.Event.Data))).Ids));

        inngest.AddFunction<RunEvent>(
            new FunctionOptions { Id = "send-dedupe", Triggers = [Trigger.Event(Dedupe)] },
            static async (context, _) =>
            {
                var id = $"dedupe-{context.Event.Data.TestId}";
                var first = await context.Step.SendEventAsync("first", new InngestEvent(Received, new ValueEvent(context.Event.Data.TestId, 1)) { Id = id });
                var second = await context.Step.SendEventAsync("second", new InngestEvent(Received, new ValueEvent(context.Event.Data.TestId, 2)) { Id = id });
                return new TwoSends(first.Ids, second.Ids);
            });

        inngest.AddFunction<RunEvent>(
            new FunctionOptions { Id = "send-described", Triggers = [Trigger.Event(Described)] },
            static async (context, _) =>
                new SentIds((await context.Step.SendEventAsync(
                    "described",
                    new InngestEvent(Received, new ValueEvent(context.Event.Data.TestId, 5))
                    {
                        Id = $"described-{context.Event.Data.TestId}",
                        Timestamp = DescribedTimestamp,
                        Version = "2026-01-02.1",
                    })).Ids));
    }
}
