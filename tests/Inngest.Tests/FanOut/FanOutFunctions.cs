namespace Inngest.Tests.FanOut;

public sealed record Invoice(string Id, decimal Amount);

public sealed record Receipt(string InvoiceId, string Reference);

// What the chunk function charged, shared by the tests that register it.
public sealed class Charger
{
    private readonly object _gate = new();
    private readonly List<string> _charged = [];
    private int _attempts;

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

    // How many times a function was set up to run, for the tests of how often it is tried.
    public int Attempts => Volatile.Read(ref _attempts);

    public void Attempt() => Interlocked.Increment(ref _attempts);

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

// A chunk function configured by attributes, as the guide shows: an id, flow control and no trigger.
[InngestFunction("attributed-chunk", Retries = 2)]
[Concurrency(3)]
public sealed class AttributedChunk(Charger charger) : FanOutFunction<Invoice, Receipt>
{
    protected override Task<Receipt> ProcessAsync(Invoice item, CancellationToken cancellationToken) =>
        Task.FromResult(charger.Charge(item));
}

// A chunk function whose window is not valid, which counts how often a run of it starts.
public sealed class NoWindowChunk(Charger charger) : FanOutFunction<Invoice, Receipt>
{
    protected override int? MaxParallelism
    {
        get
        {
            charger.Attempt();
            return 0;
        }
    }

    protected override Task<Receipt> ProcessAsync(Invoice item, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("The chunk has no window to run in.");
}
