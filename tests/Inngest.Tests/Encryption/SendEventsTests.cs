using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Inngest.Encryption;

namespace Inngest.Tests.Encryption;

// WrapSendEventAsync: the field of each event's data that EventEncryptionField names becomes an envelope on its way to the client.
public sealed class SendEventsTests
{
    private static readonly LibSodiumEncryptionService Service = new([JsVectors.Key]);

    [Fact]
    public async Task The_encrypted_field_of_an_events_data_is_encrypted_and_the_rest_of_the_event_is_kept()
    {
        var original = new InngestEvent("app/order.paid", new OrderPaid(7, new Card("4242"), "eu"))
        {
            Id = "evt-1",
            Version = "2026-10",
            User = new { Id = 5 },
            Timestamp = new DateTimeOffset(2026, 10, 29, 10, 0, 0, TimeSpan.Zero),
        };

        var sent = (await SendAsync(EncryptionHarness.Middleware(), [original])).ShouldHaveSingleItem();

        var data = (JsonElement)sent.Data!;
        data.EnumerateObject().Select(property => property.Name).ShouldBe(["orderId", "encrypted", "region"]);
        data.GetProperty("orderId").GetInt32().ShouldBe(7);
        data.GetProperty("region").GetString().ShouldBe("eu");
        data.GetProperty("encrypted").GetProperty("__STRATEGY__").GetString().ShouldBe("inngest/libsodium");
        EncryptionHarness.Decrypt(Service, data.GetProperty("encrypted").GetRawText()).ShouldBe("""{"cardNumber":"4242"}""");
        (sent with { Data = original.Data }).ShouldBe(original);
    }

    [Fact]
    public async Task Another_field_can_be_the_one_that_is_encrypted()
    {
        var middleware = EncryptionHarness.Middleware(options => options.EventEncryptionField = "secret");
        var original = new InngestEvent("app/thing", new Dictionary<string, object?> { ["encrypted"] = "stays", ["secret"] = new { A = 1 } });

        var data = (JsonElement)(await SendAsync(middleware, [original])).Single().Data!;

        data.GetProperty("encrypted").GetString().ShouldBe("stays");
        EncryptionHarness.Decrypt(Service, data.GetProperty("secret").GetRawText()).ShouldBe("""{"a":1}""");
    }

    [Fact]
    public async Task The_field_is_matched_against_the_name_the_serializer_writes_and_not_the_name_of_the_property()
    {
        var original = new InngestEvent("app/thing", new Differently("kept", "hidden"));

        var byJsonName = (JsonElement)(await SendAsync(EncryptionHarness.Middleware(options => options.EventEncryptionField = "secretPart"), [original])).Single().Data!;
        var byPropertyName = await SendAsync(EncryptionHarness.Middleware(options => options.EventEncryptionField = "SecretPart"), [original]);

        byJsonName.GetProperty("plain").GetString().ShouldBe("kept");
        EncryptionHarness.Decrypt(Service, byJsonName.GetProperty("secretPart").GetRawText()).ShouldBe("\"hidden\"");
        byPropertyName.Single().ShouldBeSameAs(original);
    }

    [Fact]
    public async Task Events_without_the_field_are_sent_as_they_are()
    {
        IReadOnlyList<InngestEvent> events =
        [
            new InngestEvent("app/a", new { Plain = 1 }),
            new InngestEvent("app/b"),
            new InngestEvent("app/c", new[] { 1, 2, 3 }),
            new InngestEvent("app/d", JsonDocument.Parse("""{"other":true}""").RootElement),
        ];

        var sent = await SendAsync(EncryptionHarness.Middleware(), events);

        sent.ShouldBeSameAs(events);
    }

    [Fact]
    public async Task Only_the_events_that_have_the_field_are_replaced()
    {
        IReadOnlyList<InngestEvent> events =
        [
            new InngestEvent("app/a", new { Plain = 1 }),
            new InngestEvent("app/b", new { Encrypted = "secret" }),
            new InngestEvent("app/c"),
        ];

        var sent = await SendAsync(EncryptionHarness.Middleware(), events);

        sent.Count.ShouldBe(3);
        sent[0].ShouldBeSameAs(events[0]);
        sent[1].ShouldNotBeSameAs(events[1]);
        sent[2].ShouldBeSameAs(events[2]);
        EncryptionHarness.Decrypt(Service, ((JsonElement)sent[1].Data!).GetProperty("encrypted").GetRawText()).ShouldBe("\"secret\"");
    }

    [Fact]
    public async Task Null_and_empty_text_in_the_field_are_encrypted_as_the_typescript_package_does()
    {
        IReadOnlyList<InngestEvent> events =
        [
            new InngestEvent("app/a", new { Encrypted = (object?)null }),
            new InngestEvent("app/b", new { Encrypted = "" }),
        ];

        var sent = await SendAsync(EncryptionHarness.Middleware(), events);

        EncryptionHarness.Decrypt(Service, ((JsonElement)sent[0].Data!).GetProperty("encrypted").GetRawText()).ShouldBe("null");
        EncryptionHarness.Decrypt(Service, ((JsonElement)sent[1].Data!).GetProperty("encrypted").GetRawText()).ShouldBe("\"\"");
    }

    [Fact]
    public async Task Data_given_as_json_is_encrypted_in_the_same_way()
    {
        var original = new InngestEvent("app/a", JsonDocument.Parse("""{"id":1,"encrypted":{"x":[1,2]}}""").RootElement);

        var data = (JsonElement)(await SendAsync(EncryptionHarness.Middleware(), [original])).Single().Data!;

        data.GetProperty("id").GetInt32().ShouldBe(1);
        EncryptionHarness.Decrypt(Service, data.GetProperty("encrypted").GetRawText()).ShouldBe("""{"x":[1,2]}""");
    }

    [Fact]
    public async Task Decrypt_only_sends_the_events_as_they_are()
    {
        IReadOnlyList<InngestEvent> events = [new InngestEvent("app/a", new { Encrypted = "secret" })];

        var sent = await SendAsync(EncryptionHarness.Middleware(options => options.DecryptOnly = true), events);

        sent.ShouldBeSameAs(events);
    }

    [Fact]
    public async Task A_missing_event_is_left_for_the_client_to_refuse()
    {
        IReadOnlyList<InngestEvent> events = [null!, new InngestEvent("app/a", new { Encrypted = 1 })];

        var sent = await SendAsync(EncryptionHarness.Middleware(), events);

        sent[0].ShouldBeNull();
        sent[1].ShouldNotBeSameAs(events[1]);
    }

    [Fact]
    public async Task The_app_decides_the_names_by_its_serializer_options()
    {
        var original = new InngestEvent("app/a", new Differently("kept", "hidden"));
        var snake = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
        };
        var middleware = new EncryptionMiddleware(new PayloadEncryptor(new EncryptionOptions { Key = JsVectors.Key, EventEncryptionField = "secret_part" }, snake));

        var data = (JsonElement)(await SendAsync(middleware, [original])).Single().Data!;

        EncryptionHarness.Decrypt(Service, data.GetProperty("secret_part").GetRawText()).ShouldBe("\"hidden\"");
    }

    [Fact]
    public async Task What_is_sent_is_read_back_by_the_transform_that_opens_incoming_events()
    {
        var original = new OrderPaid(7, new Card("4242"), "eu");
        var middleware = EncryptionHarness.Middleware();

        var sent = (await SendAsync(middleware, [new InngestEvent("app/order.paid", original)])).Single();
        var received = middleware.Event($$"""{"name":"app/order.paid","data":{{((JsonElement)sent.Data!).GetRawText()}},"id":"x"}""");

        received.ShouldBeJson("""{"name":"app/order.paid","data":{"orderId":7,"encrypted":{"cardNumber":"4242"},"region":"eu"},"id":"x"}""");
    }

    private static async Task<IReadOnlyList<InngestEvent>> SendAsync(InngestMiddleware middleware, IReadOnlyList<InngestEvent> events)
    {
        IReadOnlyList<InngestEvent>? sent = null;
        await middleware.WrapSendEventAsync(
            null,
            events,
            next =>
            {
                sent = next;
                return Task.FromResult(new SendEventsResult(["id"]));
            },
            TestContext.Current.CancellationToken);
        return sent!;
    }

    private sealed record OrderPaid(int OrderId, Card Encrypted, string Region);

    private sealed record Card(string CardNumber);

    private sealed record Differently(string Plain, string SecretPart);
}
