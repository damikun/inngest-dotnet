using Inngest;

namespace Replay;

public sealed class FulfillOrder(
    IInventory inventory, IPayments payments, IMailer mailer, IWarehouse warehouse)
    : InngestFunction<OrderPlaced>
{
    public override async Task<object?> ExecuteAsync(
        FunctionContext<OrderPlaced> context, CancellationToken cancellationToken)
    {
        var order = context.Event.Data;

        var reservation = await context.Step.RunAsync("reserve-stock",
            ct => inventory.ReserveAsync(order.OrderId, ct));

        var payment = await context.Step.RunAsync("charge-card",
            ct => payments.ChargeAsync(order.CustomerId, order.Total, ct));

        await context.Step.SleepAsync("cooling-off", TimeSpan.FromMinutes(30));

        await Task.WhenAll(
            context.Step.RunAsync("send-receipt",
                ct => mailer.SendReceiptAsync(order.OrderId, payment.PaymentId, ct)),
            context.Step.RunAsync("ship-order",
                ct => warehouse.ShipAsync(reservation.ReservationId, ct)));

        return new OrderFulfilled(order.OrderId, payment.PaymentId);
    }
}
