using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;

namespace Inngest.EntityFrameworkCore.Tests;

// Whatever would make Inngest turn an event away is found while the code that added it is still on the stack, and
// the serialization is the SDK's, so what can be sent can be added.
public sealed partial class EventValidationTests
{
    [Fact]
    public async Task An_event_without_a_name_is_refused_when_it_is_added()
    {
        using var database = SqliteDatabase.Single();
        await using var host = new OutboxHost(database);
        await using var context = host.CreateContext();

        Should.Throw<ArgumentException>(() => context.AddInngestEvent(new InngestEvent(" ", new { }))).Message.ShouldContain("needs a name");
        Should.Throw<ArgumentException>(() => context.AddInngestEvent(new InngestEvent("inngest/function.failed", new { }))).Message.ShouldContain("reserved");
        Should.Throw<ArgumentNullException>(() => context.AddInngestEvent((InngestEvent)null!));
        context.ChangeTracker.Entries().ShouldBeEmpty();
    }

    [Fact]
    public async Task Data_that_is_not_an_object_is_refused_when_it_is_added()
    {
        using var database = SqliteDatabase.Single();
        await using var host = new OutboxHost(database);
        await using var context = host.CreateContext();

        Should.Throw<ArgumentException>(() => context.AddInngestEvent(new InngestEvent("shop/list", new[] { 1, 2 }))).Message.ShouldContain("must serialize to a JSON object");
        Should.Throw<ArgumentException>(() => context.AddInngestEvent(new InngestEvent("shop/text", "text"))).Message.ShouldContain("must serialize to a JSON object");
        Should.Throw<ArgumentException>(() => context.AddInngestEvent(new InngestEvent("shop/user", new { }) { User = 5 })).Message.ShouldContain("user of event");
        context.ChangeTracker.Entries().ShouldBeEmpty();
    }

    [Fact]
    public async Task An_event_without_data_is_sent_with_an_empty_object()
    {
        using var database = SqliteDatabase.Single();
        await using var host = new OutboxHost(database);

        await using (var context = host.CreateContext())
        {
            context.AddInngestEvent(new InngestEvent("shop/ping"));
            await context.SaveChangesAsync();
        }

        await host.Processor.DispatchAsync(CancellationToken.None);

        host.Client.Events.ShouldHaveSingleItem().Data.ShouldBeOfType<JsonElement>().GetRawText().ShouldBe("{}");
    }

    [Fact]
    public async Task Text_longer_than_the_columns_is_refused_when_it_is_added()
    {
        using var database = SqliteDatabase.Single();
        await using var host = new OutboxHost(database);
        await using var context = host.CreateContext();
        var tooLong = new string('x', 256);

        Should.Throw<ArgumentException>(() => context.AddInngestEvent(new OrderPlaced("A-1", 1), id: tooLong)).Message.ShouldContain("id of event");
        Should.Throw<ArgumentException>(() => context.AddInngestEvent(new InngestEvent("shop/" + tooLong, new { }))).Message.ShouldContain("name of event");
        Should.Throw<ArgumentException>(() => context.AddInngestEvent(new InngestEvent("shop/ok", new { }) { Version = tooLong })).Message.ShouldContain("version of event");
    }

    [Fact]
    public async Task Sessions_are_checked_as_the_client_checks_them()
    {
        using var database = SqliteDatabase.Single();
        await using var host = new OutboxHost(database);
        await using var context = host.CreateContext();

        Should.Throw<ArgumentException>(() => context.AddInngestEvent(new InngestEvent("shop/x", new { })
        {
            Meta = new EventMeta { Sessions = new Dictionary<string, string?> { [""] = "id" } },
        })).Message.ShouldContain("empty key");
        Should.Throw<ArgumentException>(() => context.AddInngestEvent(new InngestEvent("shop/x", new { })
        {
            Meta = new EventMeta { Sessions = new Dictionary<string, string?> { ["ticket"] = "" } },
        })).Message.ShouldContain("empty id");
        Should.Throw<ArgumentException>(() => context.AddInngestEvent(new InngestEvent("shop/x", new { })
        {
            Meta = new EventMeta { PropagatedSessions = new Dictionary<string, string> { ["ticket"] = "t" } },
        })).Message.ShouldContain("PropagatedSessions");
    }

    [Fact]
    public async Task Data_is_written_with_the_serializer_options_of_the_SDK()
    {
        using var database = SqliteDatabase.Single();
        await using var host = new OutboxHost(database, inngest: options => options.SerializerOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            Converters = { new JsonStringEnumConverter() },
        });

        await using (var context = host.CreateContext())
        {
            context.AddInngestEvent(new InngestEvent("shop/size", new { OrderNumber = "A-1", Size = DayOfWeek.Friday }));
            await context.SaveChangesAsync();
        }

        (await host.RowsAsync()).ShouldHaveSingleItem().Data.ShouldBe("""{"order_number":"A-1","size":"Friday"}""");
    }

    [Fact]
    public async Task Events_are_serialized_from_source_generated_metadata_without_reflection()
    {
        using var database = SqliteDatabase.Single();

        // Only the generated context knows types, as in an app that trims or compiles ahead of time.
        await using var host = new OutboxHost(database, inngest: options => options.SerializerOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            TypeInfoResolver = ShopJson.Default,
        });

        await using var context = host.CreateContext();
        var message = context.AddInngestEvent(new OrderPlaced("A-1", 10));

        message.Data.ShouldBe("""{"order_number":"A-1","total":10}""");

        // A type the context does not know fails here, the way it would when the client sends it, and leaves nothing behind.
        Should.Throw<NotSupportedException>(() => context.AddInngestEvent(new OrderShipped("A-1")));
        context.ChangeTracker.Entries<InngestOutboxMessage>().Count().ShouldBe(1);
    }

    [Fact]
    public void Event_ids_are_ULIDs_that_sort_by_the_time_they_were_made()
    {
        OutboxEventId.New(DateTimeOffset.UnixEpoch)[..10].ShouldBe("0000000000");
        OutboxEventId.New(DateTimeOffset.FromUnixTimeMilliseconds(1_000_000_000_000))[..10].ShouldBe("00X3AAA400");

        var earlier = OutboxEventId.New(DateTimeOffset.FromUnixTimeMilliseconds(1_785_000_000_000));
        var later = OutboxEventId.New(DateTimeOffset.FromUnixTimeMilliseconds(1_785_000_000_001));
        string.CompareOrdinal(earlier, later).ShouldBeLessThan(0);
        earlier[..10].ShouldBe("01KYD4MYG0");

        var ids = Enumerable.Range(0, 5000).Select(_ => OutboxEventId.New(DateTimeOffset.UnixEpoch)).ToList();
        ids.Distinct().Count().ShouldBe(ids.Count);
        ids.ShouldAllBe(id => Ulid().IsMatch(id));
    }

    [GeneratedRegex("^[0-7][0-9A-HJKMNP-TV-Z]{25}$")]
    private static partial Regex Ulid();
}

[JsonSerializable(typeof(OrderPlaced))]
internal sealed partial class ShopJson : JsonSerializerContext;
