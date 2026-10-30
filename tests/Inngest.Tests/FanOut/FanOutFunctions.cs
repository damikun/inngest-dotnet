namespace Inngest.Tests.FanOut;

public sealed record Invoice(string Id, decimal Amount);

public sealed record Receipt(string InvoiceId, string Reference);

// What the chunk function charged, shared by the tests that register it.
public sealed class Charger
{
    private readonly object _gate = new();
    private readonly List<string> _charged = [];

    public IReadOnlyList<string> Charged
    {
        get
        {
            lock (_gate)
            {
                return [.. _charged];
            }
        }
    }

    public Receipt Charge(Invoice invoice)
    {
        lock (_gate)
        {
            _charged.Add(invoice.Id);
        }

        return invoice.Id.StartsWith("bad", StringComparison.Ordinal)
            ? throw new NonRetriableException($"Card declined for {invoice.Id}")
            : new Receipt(invoice.Id, $"receipt-{invoice.Id}");
    }
}

// Runs the chunks of a list of invoices, two items at a time.
public sealed class ChargeInvoices(Charger charger) : FanOutFunction<Invoice, Receipt>
{
    protected override int? MaxParallelism => 2;

    protected override Task<Receipt> ProcessAsync(Invoice item, CancellationToken cancellationToken) =>
        Task.FromResult(charger.Charge(item));
}
