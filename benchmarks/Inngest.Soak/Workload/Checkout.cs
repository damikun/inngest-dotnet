using System.Diagnostics;

namespace Inngest.Soak.Workload;

public sealed record OrderPlaced(string OrderId, long TotalCents, IReadOnlyList<OrderLine> Lines);

public sealed record OrderLine(string Sku, int Quantity);

public sealed record Reservation(string OrderId, int Items);

public sealed record Charge(string OrderId, long AmountCents, string Reference);

public sealed record Points(string OrderId, int Awarded);

public sealed record Shipment(string OrderId, string TrackingNumber);

public sealed record Receipt(string OrderId, long ChargedCents, int Points, string TrackingNumber);

// A run of eight calls that between them end an invocation in every way the engine knows: a step executed at once,
// parallel steps reported, a requested step executed, a sleep reported, an event sent from a step, and the output.
// Every call but the last leaves the function waiting on a step that never completes in that invocation.
public sealed class Checkout : InngestFunction<OrderPlaced>
{
    public const string Id = "checkout";
    public const string EventName = "shop/order.placed";

    public override async Task<object?> ExecuteAsync(FunctionContext<OrderPlaced> context, CancellationToken cancellationToken)
    {
        var order = context.Event.Data;
        var step = context.Step;

        var reservation = await step.RunAsync("reserve-stock", () => new Reservation(order.OrderId, order.Lines.Sum(line => line.Quantity)));

        var charging = step.RunAsync("charge-card", async ct =>
        {
            // Real handlers do I/O; this one leaves the thread the way an awaited HTTP call would.
            await Task.Yield();
            return new Charge(order.OrderId, order.TotalCents, $"ch_{order.OrderId}_{reservation.Items}");
        });
        var awarding = step.RunAsync("award-points", () => new Points(order.OrderId, (int)(order.TotalCents / 100)));
        await Task.WhenAll(charging, awarding);

        await step.SleepAsync("cool-off", TimeSpan.FromHours(1));
        var shipment = await step.RunAsync("ship", () => new Shipment(order.OrderId, $"TRK-{order.OrderId}"));
        await step.SendEventAsync("announce", new InngestEvent("shop/order.shipped", new { order.OrderId, shipment.TrackingNumber }));

        var charge = await charging;
        var points = await awarding;
        return new Receipt(order.OrderId, charge.AmountCents, points.Awarded, shipment.TrackingNumber);
    }
}

// The kind of middleware apps write: it times step handlers. It also counts them, so the soak can check that every
// executed step went through it.
public sealed class StepTimingMiddleware : InngestMiddleware
{
    private static long _executed;
    private static long _elapsedTicks;

    public static long Executed => Interlocked.Read(ref _executed);

    public static TimeSpan Elapsed => TimeSpan.FromTicks(Interlocked.Read(ref _elapsedTicks));

    public override async Task<object?> WrapStepHandlerAsync(FunctionContext context, StepInfo step, Func<Task<object?>> next)
    {
        var started = Stopwatch.GetTimestamp();
        try
        {
            return await next().ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Increment(ref _executed);
            Interlocked.Add(ref _elapsedTicks, Stopwatch.GetElapsedTime(started).Ticks);
        }
    }
}
