using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace Inngest.EntityFrameworkCore.Tests;

// What the outbox is for: an event reaches Inngest if the work it belongs to committed, and only then.
public sealed class DeliveryTests
{
    [Fact]
    public async Task A_rolled_back_transaction_sends_nothing()
    {
        using var database = SqliteDatabase.Single();
        await using var host = new OutboxHost(database);

        await using (var context = host.CreateContext())
        {
            await using var transaction = await context.Database.BeginTransactionAsync();
            context.Orders.Add(new Order { Number = "A-1", Total = 10 });
            context.AddInngestEvent(new OrderPlaced("A-1", 10));
            await context.SaveChangesAsync();
            await transaction.RollbackAsync();
        }

        (await host.Processor.DispatchAsync(CancellationToken.None)).ShouldBe(0);
        host.Client.Requests.ShouldBeEmpty();
        (await host.RowsAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_save_that_fails_stores_neither_the_data_nor_the_event()
    {
        using var database = SqliteDatabase.Single();
        await using var host = new OutboxHost(database);
        await host.PlaceOrderAsync("A-1");
        await host.Processor.DispatchAsync(CancellationToken.None);

        await using (var context = host.CreateContext())
        {
            context.Orders.Add(new Order { Number = "A-1", Total = 99 });
            context.AddInngestEvent(new OrderPlaced("A-1", 99));
            await Should.ThrowAsync<DbUpdateException>(() => context.SaveChangesAsync());
        }

        (await host.Processor.DispatchAsync(CancellationToken.None)).ShouldBe(0);
        host.Client.Events.Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_committed_save_sends_the_event_once_under_its_own_id()
    {
        using var database = SqliteDatabase.Single();
        await using var host = new OutboxHost(database);

        var message = await host.PlaceOrderAsync("A-1", total: 12.5m);
        (await host.Processor.DispatchAsync(CancellationToken.None)).ShouldBe(1);
        (await host.Processor.DispatchAsync(CancellationToken.None)).ShouldBe(0);

        var sent = host.Client.Events.ShouldHaveSingleItem();
        sent.Id.ShouldBe(message.Id);
        message.Id.Length.ShouldBe(26);
        sent.Name.ShouldBe("shop/order.placed");
        sent.Version.ShouldBe("2026-10-01");
        sent.Timestamp.ShouldBe(OutboxHost.Start);
        sent.Data.ShouldBeOfType<JsonElement>().GetRawText().ShouldBe("""{"orderNumber":"A-1","total":12.5}""");

        var row = (await host.RowsAsync()).ShouldHaveSingleItem();
        row.SentAt.ShouldBe(OutboxHost.Start.UtcDateTime);
        row.SentAt!.Value.Kind.ShouldBe(DateTimeKind.Utc);
        row.Attempts.ShouldBe(1);
        row.LeaseToken.ShouldBeNull();
        row.LeaseExpiresAt.ShouldBeNull();
        row.LastError.ShouldBeNull();
    }

    [Fact]
    public async Task The_events_of_a_save_go_out_in_one_request()
    {
        using var database = SqliteDatabase.Single();
        await using var host = new OutboxHost(database);

        await using (var context = host.CreateContext())
        {
            context.Orders.Add(new Order { Number = "A-1" });
            context.AddInngestEvent(new OrderPlaced("A-1", 10));
            context.AddInngestEvent(new OrderShipped("A-1"));
            await context.SaveChangesAsync();
        }

        await host.Processor.DispatchAsync(CancellationToken.None);

        var request = host.Client.Requests.ShouldHaveSingleItem();
        request.Select(@event => @event.Name).ShouldBe(["shop/order.placed", "shop/order.shipped"], ignoreOrder: true);
    }

    [Fact]
    public async Task An_event_keeps_the_time_it_was_added_not_the_time_it_was_sent()
    {
        using var database = SqliteDatabase.Single();
        await using var host = new OutboxHost(database);

        await host.PlaceOrderAsync("A-1");
        host.Time.Advance(TimeSpan.FromHours(3));
        await host.Processor.DispatchAsync(CancellationToken.None);

        host.Client.Events.ShouldHaveSingleItem().Timestamp.ShouldBe(OutboxHost.Start);
    }

    [Fact]
    public async Task A_named_event_keeps_its_id_user_timestamp_version_and_sessions()
    {
        using var database = SqliteDatabase.Single();
        await using var host = new OutboxHost(database);
        var happened = new DateTimeOffset(2026, 10, 29, 8, 30, 0, TimeSpan.FromHours(2));

        await using (var context = host.CreateContext())
        {
            context.AddInngestEvent(new InngestEvent("support/ticket.opened", new { TicketId = "t_42" })
            {
                Id = "ticket-opened-t_42",
                User = new { Plan = "pro" },
                Timestamp = happened,
                Version = "3",
                Meta = new EventMeta { Sessions = new Dictionary<string, string?> { ["ticket_id"] = "t_42", ["old"] = null } },
            });
            await context.SaveChangesAsync();
        }

        await host.Processor.DispatchAsync(CancellationToken.None);

        var sent = host.Client.Events.ShouldHaveSingleItem();
        sent.Id.ShouldBe("ticket-opened-t_42");
        sent.Name.ShouldBe("support/ticket.opened");
        sent.Timestamp.ShouldBe(happened);
        sent.Version.ShouldBe("3");
        sent.Data.ShouldBeOfType<JsonElement>().GetRawText().ShouldBe("""{"ticketId":"t_42"}""");
        sent.User.ShouldBeOfType<JsonElement>().GetRawText().ShouldBe("""{"plan":"pro"}""");
        sent.Meta!.Sessions!["ticket_id"].ShouldBe("t_42");
        sent.Meta.Sessions.ContainsKey("old").ShouldBeTrue();
        sent.Meta.Sessions["old"].ShouldBeNull();
    }

    [Fact]
    public async Task An_id_given_twice_fails_the_second_save_instead_of_sending_a_second_event()
    {
        using var database = SqliteDatabase.Single();
        await using var host = new OutboxHost(database);

        await using (var context = host.CreateContext())
        {
            context.AddInngestEvent(new OrderPlaced("A-1", 10), id: "order-placed-A-1");
            await context.SaveChangesAsync();
        }

        await using (var context = host.CreateContext())
        {
            context.AddInngestEvent(new OrderPlaced("A-1", 10), id: "order-placed-A-1");
            await Should.ThrowAsync<DbUpdateException>(() => context.SaveChangesAsync());
        }

        await host.Processor.DispatchAsync(CancellationToken.None);
        host.Client.Events.ShouldHaveSingleItem().Id.ShouldBe("order-placed-A-1");
    }

    [Fact]
    public async Task Events_come_out_oldest_first()
    {
        using var database = SqliteDatabase.Single();
        await using var host = new OutboxHost(database);

        foreach (var number in new[] { "A-1", "A-2", "A-3" })
        {
            await host.PlaceOrderAsync(number);
            host.Time.Advance(TimeSpan.FromSeconds(1));
        }

        await host.Processor.DispatchAsync(CancellationToken.None);

        host.Client.Events.Select(@event => ((JsonElement)@event.Data!).GetProperty("orderNumber").GetString())
            .ShouldBe(["A-1", "A-2", "A-3"]);
    }

    [Fact]
    public async Task A_batch_is_as_large_as_the_options_say()
    {
        using var database = SqliteDatabase.Single();
        await using var host = new OutboxHost(database, options => options.BatchSize = 2);
        foreach (var number in new[] { "A-1", "A-2", "A-3", "A-4", "A-5" })
        {
            await host.PlaceOrderAsync(number);
        }

        while (await host.Processor.DispatchAsync(CancellationToken.None) > 0)
        {
        }

        host.Client.Requests.Select(request => request.Count).ShouldBe([2, 2, 1]);
    }
}
