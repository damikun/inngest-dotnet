using Microsoft.EntityFrameworkCore;

namespace Inngest.EntityFrameworkCore.Tests;

public sealed class Order
{
    public int Id { get; set; }

    public string Number { get; set; } = string.Empty;

    public decimal Total { get; set; }
}

public sealed record OrderPlaced(string OrderNumber, decimal Total) : IInngestEventData
{
    public static string EventName => "shop/order.placed";

    public static string? EventVersion => "2026-10-01";
}

public sealed record OrderShipped(string OrderNumber) : IInngestEventData
{
    public static string EventName => "shop/order.shipped";
}

// The app's own context, with the outbox in its model next to its data.
public sealed class ShopContext(DbContextOptions<ShopContext> options) : DbContext(options)
{
    public DbSet<Order> Orders => Set<Order>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // A second order with a number that exists fails the save, which is how the tests make one fail.
        modelBuilder.Entity<Order>().HasIndex(order => order.Number).IsUnique();
        modelBuilder.AddInngestOutbox();
    }
}

// A context that never mapped the outbox.
public sealed class PlainContext(DbContextOptions<PlainContext> options) : DbContext(options)
{
    public DbSet<Order> Orders => Set<Order>();
}

// A context that moves the outbox table to another name and schema.
public sealed class RenamedContext(DbContextOptions<RenamedContext> options) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder) =>
        modelBuilder.AddInngestOutbox(outbox => outbox.ToTable("Outbox", "inngest"));
}

// A context that adds the interceptor in OnConfiguring, where there is no service provider to pass.
public sealed class ConfiguringContext(DbContextOptions<ConfiguringContext> options) : DbContext(options)
{
    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder) => optionsBuilder.UseInngestOutbox();

    protected override void OnModelCreating(ModelBuilder modelBuilder) => modelBuilder.AddInngestOutbox();
}
