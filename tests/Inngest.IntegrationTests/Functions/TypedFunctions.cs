using Inngest.IntegrationTests.Harness;

namespace Inngest.IntegrationTests.Functions;

// Typed events, attribute classes and invoke by class, end to end.
internal sealed record TypedOrderPlaced(string TestId, int Amount) : IInngestEventData
{
    public static string EventName => "itests/typed.order-placed";
}

internal sealed record TypedOrderCancelled(string TestId) : IInngestEventData
{
    public static string EventName => "itests/typed.order-cancelled";
}

internal sealed record TypedOrderConfirmed(string TestId, int Total) : IInngestEventData
{
    public static string EventName => "itests/typed.order-confirmed";

    public static string? EventVersion => "2026-10-29";
}

internal sealed record PriceRequest(string TestId, int Amount);

internal sealed record PriceQuote(int Total, string Currency);

internal sealed record TypedOrderResult(PriceQuote Quote, string ConfirmationId);

// Registered by its attributes. The app sends its event with SendAsync<T>.
[InngestFunction("typed-order", Name = "Typed order", Retries = 1)]
[EventTrigger<TypedOrderPlaced>]
[Concurrency(5, Key = "event.data.testId")]
[CancelOn<TypedOrderCancelled>(Match = "data.testId", Timeout = "10m")]
internal sealed class TypedOrderFunction : InngestFunction<TypedOrderPlaced, TypedOrderResult>
{
    public override async Task<TypedOrderResult> ExecuteAsync(FunctionContext<TypedOrderPlaced> context, CancellationToken cancellationToken)
    {
        var order = context.Event.Data;
        var quote = await context.Step.InvokeAsync<TypedPricingFunction, PriceQuote>("price", new PriceRequest(order.TestId, order.Amount));
        var sent = await context.Step.SendEventAsync("confirm", new TypedOrderConfirmed(order.TestId, quote.Total));
        return new TypedOrderResult(quote, sent.Ids[0]);
    }
}

// Only ever invoked by its class, so it has no trigger.
[InngestFunction("typed-pricing")]
internal sealed class TypedPricingFunction(Probe probe) : InngestFunction<PriceRequest, PriceQuote>
{
    public override Task<PriceQuote> ExecuteAsync(FunctionContext<PriceRequest> context, CancellationToken cancellationToken)
    {
        probe.For(context.Event.Data.TestId).Record("priced", context.Event.Data.Amount);
        return Task.FromResult(new PriceQuote(context.Event.Data.Amount * 2, "EUR"));
    }
}

// Triggered by the typed event that TypedOrderFunction sends from a step.
[InngestFunction("typed-confirmation")]
[EventTrigger<TypedOrderConfirmed>]
internal sealed class TypedConfirmationFunction(Probe probe) : InngestFunction<TypedOrderConfirmed, int>
{
    public override Task<int> ExecuteAsync(FunctionContext<TypedOrderConfirmed> context, CancellationToken cancellationToken)
    {
        probe.For(context.Event.Data.TestId).Record("confirmed", $"{context.Event.Data.Total} v{context.Event.Version}");
        return Task.FromResult(context.Event.Data.Total);
    }
}
