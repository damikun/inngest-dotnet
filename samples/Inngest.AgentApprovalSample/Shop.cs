using System.Globalization;

namespace Inngest.AgentApprovalSample;

public sealed record Order(int Id, string Customer, string Email, string Item, decimal Total, string Status, decimal Refunded = 0);

public sealed record SentEmail(int OrderId, string To, string Subject, string Body, DateTimeOffset SentAt);

public sealed class Shop
{
    private readonly Lock _gate = new();
    private readonly Dictionary<int, Order> _orders = new[]
    {
        new Order(1001, "Anna Novak", "anna@example.com", "Espresso grinder", 129.00m, "Delivered"),
        new Order(1002, "Ben Carter", "ben@example.com", "Pour-over set", 39.90m, "Delivered"),
        new Order(1003, "Chloé Martin", "chloe@example.com", "Glass kettle", 49.90m, "Delivered"),
        new Order(1004, "David Kim", "david@example.com", "Coffee beans, 2 kg", 36.00m, "Shipped"),
        new Order(1005, "Eva Horváth", "eva@example.com", "Milk frother", 59.00m, "Refunded", 59.00m),
    }.ToDictionary(order => order.Id);
    private readonly List<SentEmail> _outbox = [];

    public IReadOnlyList<Order> Orders
    {
        get
        {
            lock (_gate)
            {
                return [.. _orders.Values.OrderBy(order => order.Id)];
            }
        }
    }

    public IReadOnlyList<SentEmail> Outbox
    {
        get
        {
            lock (_gate)
            {
                return [.. _outbox.AsEnumerable().Reverse()];
            }
        }
    }

    public Order? Find(int orderId)
    {
        lock (_gate)
        {
            return _orders.GetValueOrDefault(orderId);
        }
    }

    public string Refund(int orderId, decimal amount)
    {
        lock (_gate)
        {
            if (!_orders.TryGetValue(orderId, out var order))
            {
                return $"There is no order {orderId}.";
            }

            var left = order.Total - order.Refunded;
            if (amount <= 0 || amount > left)
            {
                return string.Create(CultureInfo.InvariantCulture, $"Order {orderId} can be refunded up to {left:0.00} EUR.");
            }

            var refunded = order.Refunded + amount;
            _orders[orderId] = order with { Refunded = refunded, Status = refunded == order.Total ? "Refunded" : "Partly refunded" };
            return string.Create(CultureInfo.InvariantCulture, $"Refunded {amount:0.00} EUR to {order.Customer}.");
        }
    }

    public string Send(int orderId, string subject, string body)
    {
        lock (_gate)
        {
            if (!_orders.TryGetValue(orderId, out var order))
            {
                return $"There is no order {orderId}.";
            }

            _outbox.Add(new SentEmail(orderId, order.Email, subject, body, DateTimeOffset.UtcNow));
            return $"Sent to {order.Email}.";
        }
    }
}
