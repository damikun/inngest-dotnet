// Types that guide fragments use without defining them. A page that defines a type of the same name uses its own.
using System.Text.Json;

namespace Docs.Support;

public interface IEmailSender
{
    Task<string> SendAsync(string to, string subject, CancellationToken cancellationToken);
}

// A model client of the app's own, such as a wrapper of IChatClient from Microsoft.Extensions.AI.
public interface IChatModel
{
    Task<string> CompleteAsync(string prompt, CancellationToken cancellationToken);

    IAsyncEnumerable<string> StreamAsync(string prompt, CancellationToken cancellationToken);
}

public sealed record UserSignedUp(string UserId, string Email) : Inngest.IInngestEventData
{
    public static string EventName => "user/signed-up";
}

public sealed record UserActivated(string UserId) : Inngest.IInngestEventData
{
    public static string EventName => "user/activated";
}

public sealed record OrderPlaced(string OrderId, string CustomerId, decimal Total) : Inngest.IInngestEventData
{
    public static string EventName => "shop/order.placed";
}

public interface IPaymentGateway
{
    Task<string> ChargeAsync(string customerId, decimal amount, CancellationToken cancellationToken);
}

public interface IOrderStore
{
    Task MarkPaidAsync(string orderId, string paymentId, CancellationToken cancellationToken);
}

public sealed record Invoice(string Id, string CustomerId, decimal Amount);

public interface IBilling
{
    Task<IReadOnlyList<Invoice>> GetOpenInvoicesAsync(string customerId, CancellationToken cancellationToken);

    Task<string> ChargeAsync(string invoiceId, CancellationToken cancellationToken);

    Task<decimal> SumOpenAsync(string customerId, CancellationToken cancellationToken);

    Task NotifyAsync(string customerId, CancellationToken cancellationToken);
}

public sealed record AccountScore(string AccountId, int Score);

public sealed record ScoreRequest(string AccountId);

[Inngest.InngestFunction("score-account")]
public sealed class ScoreAccount : Inngest.InngestFunction<ScoreRequest, AccountScore>
{
    public override Task<AccountScore> ExecuteAsync(Inngest.FunctionContext<ScoreRequest> context, CancellationToken cancellationToken) =>
        Task.FromResult(new AccountScore(context.Event.Data.AccountId, 0));
}

public interface IReports
{
    Task<string> BuildAsync(IReadOnlyList<AccountScore> scores, CancellationToken cancellationToken);
}

public static class Samples
{
    public static readonly JsonElement EmptyObject = JsonDocument.Parse("{}").RootElement;
}

public sealed record Profile(string UserId, string Name);

public sealed record PastOrder(string OrderId, decimal Total);

public interface IProfiles
{
    Task<Profile> LoadAsync(string userId, CancellationToken cancellationToken);
}

public interface IOrderHistory
{
    Task<IReadOnlyList<PastOrder>> LoadAsync(string userId, CancellationToken cancellationToken);
}

public sealed record Shipment(string Id, decimal WeightKg);

public sealed record Quote(string Carrier, decimal Price);

public interface ICarrier
{
    Task<Quote> QuoteAsync(Shipment shipment, CancellationToken cancellationToken);
}

public sealed record Reminder(string Id, string UserId, string Text, DateTimeOffset DueAt);

public interface IReminders
{
    Task<Reminder> GetAsync(string reminderId, CancellationToken cancellationToken);

    Task NotifyAsync(string reminderId, CancellationToken cancellationToken);
}

public sealed record ImportRow(string ImportId, int Line, IReadOnlyDictionary<string, string> Values);

public interface IRowWriter
{
    Task WriteAsync(IReadOnlyList<ImportRow> rows, CancellationToken cancellationToken);
}

public sealed record ExchangeRates(string Base, IReadOnlyDictionary<string, decimal> Rates);

public interface IInventory
{
    Task<string> ReserveAsync(string orderId, CancellationToken cancellationToken);

    Task ReleaseAsync(string reservationId, CancellationToken cancellationToken);
}

public sealed class SmtpEmailSender : IEmailSender
{
    public Task<string> SendAsync(string to, string subject, CancellationToken cancellationToken) => Task.FromResult("message-id");
}

[Inngest.InngestFunction("onboard-user")]
[Inngest.EventTrigger<UserSignedUp>]
public sealed class OnboardUser(IEmailSender email) : Inngest.InngestFunction<UserSignedUp>
{
    public override async Task<object?> ExecuteAsync(Inngest.FunctionContext<UserSignedUp> context, CancellationToken cancellationToken)
    {
        await context.Step.RunAsync("send-welcome-email", ct => email.SendAsync(context.Event.Data.Email, "Welcome!", ct));
        return null;
    }
}
