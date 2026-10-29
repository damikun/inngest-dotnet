using System.Text.Json;

namespace Inngest.IntegrationTests.Evaluation.Functions;

internal sealed record EchoResult(string Name, int Value, string? EventId);

internal sealed record EventSeen(string Name, string? Id, DateTimeOffset Timestamp, string? Version, JsonElement? User, int Value);

internal static class TriggerFunctions
{
    public const string Echo = "eval/trg.echo";
    public const string Filtered = "eval/trg.filtered";
    public const string MultiA = "eval/trg.multi.a";
    public const string MultiB = "eval/trg.multi.b";
    public const string MultiC = "eval/trg.multi.c";
    public const string Wildcard = "eval/trg.wild.";
    public const string Invoker = "eval/trg.invoker";
    public const string Meta = "eval/trg.meta";
    public const string Delayed = "eval/trg.delayed";
    public const string FanOut = "eval/trg.fanout";
    public const string InvokeOnlyId = "trg-invoke-only";

    public static void Add(IInngestBuilder inngest)
    {
        inngest.AddFunction<ValueEvent>(
            new FunctionOptions { Id = "trg-echo", Triggers = [Trigger.Event(Echo)] },
            static (context, _) =>
            {
                context.Observed().Record("ran", context.Event.Data.Value);
                return Task.FromResult<object?>(new EchoResult(context.Event.Name, context.Event.Data.Value, context.Event.Id));
            });

        inngest.AddFunction<ValueEvent>(
            new FunctionOptions { Id = "trg-filtered", Triggers = [Trigger.Event(Filtered, "event.data.value >= 100")] },
            static (context, _) =>
            {
                context.Observed().Record("ran", context.Event.Data.Value);
                return Task.FromResult<object?>(context.Event.Data.Value);
            });

        inngest.AddFunction<ValueEvent>(
            new FunctionOptions
            {
                Id = "trg-multi",
                Triggers = [Trigger.Event(MultiA), Trigger.Event(MultiB), Trigger.Event(MultiC, "event.data.value > 10")],
            },
            static (context, _) =>
            {
                context.Observed().Record("ran", context.Event.Name);
                return Task.FromResult<object?>(context.Event.Name);
            });

        inngest.AddFunction<ValueEvent>(
            new FunctionOptions { Id = "trg-wildcard", Triggers = [Trigger.Event(Wildcard + "*")] },
            static (context, _) =>
            {
                context.Observed().Record("ran", context.Event.Name);
                return Task.FromResult<object?>(context.Event.Name);
            });

        // No triggers: it runs only when another function invokes it.
        inngest.AddFunction<ValueEvent>(
            new FunctionOptions { Id = InvokeOnlyId, Triggers = [] },
            static (context, _) =>
            {
                context.Observed().Record("invoked", context.Event.Name);
                return Task.FromResult<object?>(context.Event.Data.Value * 2);
            });

        inngest.AddFunction<ValueEvent>(
            new FunctionOptions { Id = "trg-invoker", Triggers = [Trigger.Event(Invoker)] },
            static async (context, _) =>
                await context.Step.InvokeAsync<int>("call", new InvokeOptions(InvokeOnlyId) { Data = context.Event.Data }));

        inngest.AddFunction<ValueEvent>(
            new FunctionOptions { Id = "trg-meta", Triggers = [Trigger.Event(Meta)] },
            static (context, _) =>
            {
                var @event = context.Event;
                return Task.FromResult<object?>(new EventSeen(@event.Name, @event.Id, @event.Timestamp, @event.Version, @event.User, @event.Data.Value));
            });

        inngest.AddFunction<RunEvent>(
            new FunctionOptions { Id = "trg-delayed", Triggers = [Trigger.Event(Delayed)] },
            static (context, _) =>
            {
                context.Observed().Record("ran");
                return Task.FromResult<object?>(null);
            });

        inngest.AddFunction<RunEvent>(
            new FunctionOptions { Id = "trg-fanout-a", Triggers = [Trigger.Event(FanOut)] },
            static (context, _) =>
            {
                context.Observed().Record("ran", "a");
                return Task.FromResult<object?>("a");
            });

        inngest.AddFunction<RunEvent>(
            new FunctionOptions { Id = "trg-fanout-b", Triggers = [Trigger.Event(FanOut)] },
            static (context, _) =>
            {
                context.Observed().Record("ran", "b");
                return Task.FromResult<object?>("b");
            });
    }
}
