using Microsoft.Extensions.Time.Testing;

namespace Replay;

public sealed record OrderPlaced(string OrderId, string CustomerId, decimal Total);

public sealed record Reservation(string ReservationId);

public sealed record Payment(string PaymentId, decimal Amount);

public sealed record OrderFulfilled(string OrderId, string PaymentId);

public interface IInventory
{
    Task<Reservation> ReserveAsync(string orderId, CancellationToken cancellationToken);
}

public interface IPayments
{
    Task<Payment> ChargeAsync(string customerId, decimal amount, CancellationToken cancellationToken);
}

public interface IMailer
{
    Task SendReceiptAsync(string orderId, string paymentId, CancellationToken cancellationToken);
}

public interface IWarehouse
{
    Task<string> ShipAsync(string reservationId, CancellationToken cancellationToken);
}

// Fakes that take a fixed, made-up amount of time on the fake clock, so the captured timings never change.
public sealed class FakeServices(FakeTimeProvider time) : IInventory, IPayments, IMailer, IWarehouse
{
    public Task<Reservation> ReserveAsync(string orderId, CancellationToken cancellationToken)
    {
        time.Advance(TimeSpan.FromMilliseconds(142));
        return Task.FromResult(new Reservation("res_8c1f"));
    }

    public Task<Payment> ChargeAsync(string customerId, decimal amount, CancellationToken cancellationToken)
    {
        time.Advance(TimeSpan.FromMilliseconds(418));
        return Task.FromResult(new Payment("pay_31d9", amount));
    }

    public Task SendReceiptAsync(string orderId, string paymentId, CancellationToken cancellationToken)
    {
        time.Advance(TimeSpan.FromMilliseconds(236));
        return Task.CompletedTask;
    }

    public Task<string> ShipAsync(string reservationId, CancellationToken cancellationToken)
    {
        time.Advance(TimeSpan.FromMilliseconds(307));
        return Task.FromResult("trk_5b72");
    }
}
