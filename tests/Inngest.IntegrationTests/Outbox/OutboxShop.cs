using Inngest.IntegrationTests.Evaluation.Functions;
using Microsoft.EntityFrameworkCore;

namespace Inngest.IntegrationTests.Outbox;

public sealed class ShopOrder
{
    public int Id { get; set; }

    public string Number { get; set; } = string.Empty;
}

internal sealed record OrderPlaced(string TestId, string OrderNumber) : IScenarioEvent, IInngestEventData
{
    public static string EventName => "itests/outbox.placed";
}

// The app's own context, with the outbox in its model next to its data.
public sealed class ShopContext(DbContextOptions<ShopContext> options) : DbContext(options)
{
    public DbSet<ShopOrder> Orders => Set<ShopOrder>();

    protected override void OnModelCreating(ModelBuilder modelBuilder) => modelBuilder.AddInngestOutbox();
}
