namespace Inngest.Tests.Events;

// Typed events for the tests of the typed overloads, the attributes and invoke by class.
internal sealed record OrderPaid(string OrderId, decimal Amount) : IInngestEventData
{
    public static string EventName => "shop/order.paid";
}

internal sealed record OrderRefunded(string OrderId) : IInngestEventData
{
    public static string EventName => "shop/order.refunded";

    public static string? EventVersion => "2";
}

internal sealed record AccountClosed(string AccountId) : IInngestEventData
{
    public static string EventName => "shop/account.closed";
}
