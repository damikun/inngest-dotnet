using System.Net;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Text.RegularExpressions;
using Inngest.Internal;
using Microsoft.Extensions.Logging;

namespace Inngest.Tests.Client;

public sealed partial class InngestClientSendTests
{
    [GeneratedRegex(@"^(?<millis>\d+),(?<entropy>[A-Za-z0-9+/]{14}==)$")]
    private static partial Regex SeedPattern();

    [Fact]
    public async Task Posts_a_json_array_to_the_dev_server_with_the_placeholder_key()
    {
        var fixture = new ClientFixture();
        fixture.Handler.Respond(HttpStatusCode.OK, ClientFixture.Accepted);

        var result = await fixture.Client.SendAsync(new InngestEvent("app/user.created", new { Id = 1 }));

        result.Ids.ShouldBe(["01HQ0000000000000000000001"]);
        var request = fixture.Handler.Single;
        request.Method.ShouldBe(HttpMethod.Post);
        request.Uri.AbsoluteUri.ShouldBe("http://localhost:8288/e/NO_EVENT_KEY_SET");
        request.BodyText.ShouldBe("""[{"name":"app/user.created","data":{"id":1},"ts":1705586504000}]""");
    }

    [Fact]
    public async Task Sends_the_sdk_headers_and_no_authorization()
    {
        var fixture = new ClientFixture();
        fixture.Handler.Respond(HttpStatusCode.OK, ClientFixture.Accepted);

        await fixture.Client.SendAsync(new InngestEvent("app/user.created"));

        var request = fixture.Handler.Single;
        request.Header("Content-Type").ShouldBe("application/json");
        request.Header("X-Inngest-SDK").ShouldBe(SdkInfo.Header);
        request.Header("User-Agent").ShouldBe(SdkInfo.Header);
        request.Header("Authorization").ShouldBeNull();
        request.Header("X-Inngest-Env").ShouldBeNull();
        request.HeaderNames.ShouldNotContain("X-Inngest-Sync-Kind");
    }

    [Fact]
    public async Task Uses_the_named_inngest_http_client()
    {
        var fixture = new ClientFixture();
        fixture.Handler.Respond(HttpStatusCode.OK, ClientFixture.Accepted);

        await fixture.Client.SendAsync(new InngestEvent("app/user.created"));

        fixture.Factory.RequestedNames.ShouldBe(["Inngest"]);
    }

    [Fact]
    public async Task Sends_the_configured_event_key_in_the_path_and_the_environment_in_a_header()
    {
        var fixture = new ClientFixture(TestSettings.Cloud(options =>
        {
            options.EventKey = "my-event-key";
            options.Environment = "feature-x";
        }));
        fixture.Handler.Respond(HttpStatusCode.OK, ClientFixture.Accepted);

        await fixture.Client.SendAsync(new InngestEvent("app/user.created"));

        var request = fixture.Handler.Single;
        request.Uri.AbsoluteUri.ShouldBe("https://inn.gs/e/my-event-key");
        request.Header("X-Inngest-Env").ShouldBe("feature-x");
        request.Header("Authorization").ShouldBeNull();
    }

    [Fact]
    public async Task Uses_the_event_key_in_dev_mode_when_there_is_one()
    {
        var fixture = new ClientFixture(TestSettings.Create(options => options.EventKey = "local-key"));
        fixture.Handler.Respond(HttpStatusCode.OK, ClientFixture.Accepted);

        await fixture.Client.SendAsync(new InngestEvent("app/user.created"));

        fixture.Handler.Single.Uri.AbsoluteUri.ShouldBe("http://localhost:8288/e/local-key");
    }

    [Fact]
    public async Task Escapes_the_event_key_in_the_path()
    {
        var fixture = new ClientFixture(TestSettings.Create(options => options.EventKey = "a/b c?d"));
        fixture.Handler.Respond(HttpStatusCode.OK, ClientFixture.Accepted);

        await fixture.Client.SendAsync(new InngestEvent("app/user.created"));

        fixture.Handler.Single.Uri.AbsoluteUri.ShouldBe("http://localhost:8288/e/a%2Fb%20c%3Fd");
    }

    [Fact]
    public async Task Resolves_the_path_relative_to_the_event_api_address()
    {
        var withSlash = new ClientFixture(TestSettings.Create(options => options.EventApiBaseUrl = new Uri("http://gateway/inngest/")));
        var withoutSlash = new ClientFixture(TestSettings.Create(options => options.EventApiBaseUrl = new Uri("http://gateway/inngest")));
        withSlash.Handler.Respond(HttpStatusCode.OK, ClientFixture.Accepted);
        withoutSlash.Handler.Respond(HttpStatusCode.OK, ClientFixture.Accepted);

        await withSlash.Client.SendAsync(new InngestEvent("app/user.created"));
        await withoutSlash.Client.SendAsync(new InngestEvent("app/user.created"));

        withSlash.Handler.Single.Uri.AbsoluteUri.ShouldBe("http://gateway/inngest/e/NO_EVENT_KEY_SET");
        withoutSlash.Handler.Single.Uri.AbsoluteUri.ShouldBe("http://gateway/e/NO_EVENT_KEY_SET");
    }

    [Fact]
    public async Task Sends_an_event_id_seed_of_the_time_and_ten_random_bytes()
    {
        var fixture = new ClientFixture();
        fixture.Handler.Respond(HttpStatusCode.OK, ClientFixture.Accepted);

        await fixture.Client.SendAsync(new InngestEvent("app/user.created"));

        var match = SeedPattern().Match(fixture.Handler.Single.Header("X-Inngest-Event-Id-Seed")!);
        match.Success.ShouldBeTrue();
        match.Groups["millis"].Value.ShouldBe("1705586504000");
        Convert.FromBase64String(match.Groups["entropy"].Value).Length.ShouldBe(10);
    }

    [Fact]
    public async Task Uses_a_new_seed_for_every_call_and_the_clock_of_the_time_provider()
    {
        var fixture = new ClientFixture();
        fixture.Handler.Respond(HttpStatusCode.OK, ClientFixture.Accepted).Respond(HttpStatusCode.OK, ClientFixture.Accepted);

        await fixture.Client.SendAsync(new InngestEvent("app/user.created"));
        fixture.Time.Advance(TimeSpan.FromSeconds(5));
        await fixture.Client.SendAsync(new InngestEvent("app/user.created"));

        var seeds = fixture.Handler.Requests.Select(request => request.Header("X-Inngest-Event-Id-Seed")!).ToArray();
        seeds[0].ShouldStartWith("1705586504000,");
        seeds[1].ShouldStartWith("1705586509000,");
        seeds[0].Split(',')[1].ShouldNotBe(seeds[1].Split(',')[1]);
        fixture.Handler.Requests[1].BodyText.ShouldContain("\"ts\":1705586509000");
    }

    [Fact]
    public async Task Writes_every_field_of_an_event()
    {
        var fixture = new ClientFixture();
        fixture.Handler.Respond(HttpStatusCode.OK, ClientFixture.Accepted);
        var @event = new InngestEvent("order/placed", new { OrderId = "o-1", Total = 12.5 })
        {
            Id = "order-o-1",
            User = new { Email = "ada@example.com" },
            Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(1700000000123),
            Version = "2024-01-01.1",
        };

        await fixture.Client.SendAsync(@event);

        fixture.Handler.Single.BodyText.ShouldBe(
            """[{"name":"order/placed","data":{"orderId":"o-1","total":12.5},"user":{"email":"ada@example.com"},"id":"order-o-1","ts":1700000000123,"v":"2024-01-01.1"}]""");
    }

    [Fact]
    public async Task Defaults_the_data_to_an_empty_object_and_leaves_out_unset_fields()
    {
        var fixture = new ClientFixture();
        fixture.Handler.Respond(HttpStatusCode.OK, ClientFixture.Accepted);

        await fixture.Client.SendAsync(new InngestEvent("app/ping") { Id = "", Version = "" });

        fixture.Handler.Single.BodyText.ShouldBe("""[{"name":"app/ping","data":{},"ts":1705586504000}]""");
    }

    [Fact]
    public async Task Sends_several_events_in_one_request_in_order()
    {
        var fixture = new ClientFixture();
        fixture.Handler.Respond(HttpStatusCode.OK, """{"ids":["id-1","id-2","id-3"],"status":200}""");

        var result = await fixture.Client.SendAsync(
            new[] { new InngestEvent("a/one", new { N = 1 }), new InngestEvent("a/two", new { N = 2 }), new InngestEvent("a/three") });

        result.Ids.ShouldBe(["id-1", "id-2", "id-3"]);
        fixture.Handler.Single.BodyText.ShouldBe(
            """[{"name":"a/one","data":{"n":1},"ts":1705586504000},{"name":"a/two","data":{"n":2},"ts":1705586504000},{"name":"a/three","data":{},"ts":1705586504000}]""");
    }

    [Fact]
    public async Task Sends_bodies_that_outgrow_the_initial_buffer_unchanged()
    {
        var fixture = new ClientFixture();
        fixture.Handler.Respond(HttpStatusCode.OK, ClientFixture.Accepted);
        var payload = new string('x', 200_000);
        var events = Enumerable.Range(0, 10).Select(n => new InngestEvent($"app/event.{n}", new { Index = n, Payload = payload })).ToArray();

        await fixture.Client.SendAsync(events);

        using var body = JsonDocument.Parse(fixture.Handler.Single.Body);
        body.RootElement.GetArrayLength().ShouldBe(10);
        body.RootElement[9].GetProperty("name").GetString().ShouldBe("app/event.9");
        body.RootElement[9].GetProperty("data").GetProperty("payload").GetString().ShouldBe(payload);
        fixture.Handler.Single.Body.Length.ShouldBeGreaterThan(2_000_000);
    }

    [Fact]
    public async Task Accepts_a_lazy_sequence_of_events()
    {
        var fixture = new ClientFixture();
        fixture.Handler.Respond(HttpStatusCode.OK, ClientFixture.Accepted);

        await fixture.Client.SendAsync(Enumerable.Range(1, 1).Select(n => new InngestEvent($"a/event.{n}")));

        fixture.Handler.Single.BodyText.ShouldContain("a/event.1");
    }

    [Fact]
    public async Task Serializes_event_data_with_the_configured_serializer_options()
    {
        var fixture = new ClientFixture(TestSettings.Create(options =>
            options.SerializerOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
            {
                PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
                TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
            }));
        fixture.Handler.Respond(HttpStatusCode.OK, ClientFixture.Accepted);

        await fixture.Client.SendAsync(new InngestEvent("app/user.created", new { UserId = 7 }));

        fixture.Handler.Single.BodyText.ShouldContain("\"data\":{\"user_id\":7}");
    }

    [Fact]
    public async Task Serializes_event_data_with_a_source_generated_context_without_reflection()
    {
        var fixture = new ClientFixture(TestSettings.Create(options =>
            options.SerializerOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web) { TypeInfoResolver = ClientTestJsonContext.Default }));
        fixture.Handler.Respond(HttpStatusCode.OK, ClientFixture.Accepted);

        await fixture.Client.SendAsync(new InngestEvent("order/placed", new OrderPlaced("o-1", 3)) { User = new OrderPlaced("u", 0) });

        fixture.Handler.Single.BodyText.ShouldBe(
            """[{"name":"order/placed","data":{"orderId":"o-1","quantity":3},"user":{"orderId":"u","quantity":0},"ts":1705586504000}]""");
    }

    [Fact]
    public async Task Does_not_fall_back_to_reflection_for_a_type_the_context_does_not_know()
    {
        var fixture = new ClientFixture(TestSettings.Create(options =>
            options.SerializerOptions = new JsonSerializerOptions { TypeInfoResolver = ClientTestJsonContext.Default }));

        await Should.ThrowAsync<NotSupportedException>(() => fixture.Client.SendAsync(new InngestEvent("app/ping", new { Unknown = true })));

        fixture.Handler.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task Sends_data_that_is_already_a_json_element()
    {
        var fixture = new ClientFixture(TestSettings.Create(options =>
            options.SerializerOptions = new JsonSerializerOptions { TypeInfoResolver = ClientTestJsonContext.Default }));
        fixture.Handler.Respond(HttpStatusCode.OK, ClientFixture.Accepted);
        using var data = JsonDocument.Parse("""{"nested":{"list":[1,2,3]}}""");

        await fixture.Client.SendAsync(new InngestEvent("app/raw", data.RootElement));

        fixture.Handler.Single.BodyText.ShouldContain("\"data\":{\"nested\":{\"list\":[1,2,3]}}");
    }

    [Fact]
    public async Task Honours_the_encoder_of_the_serializer_options()
    {
        var fixture = new ClientFixture(TestSettings.Create(options =>
            options.SerializerOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
            {
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
            }));
        fixture.Handler.Respond(HttpStatusCode.OK, ClientFixture.Accepted);

        await fixture.Client.SendAsync(new InngestEvent("app/greeting", new { Text = "Příliš žluťoučký <kůň>" }));

        fixture.Handler.Single.BodyText.ShouldContain("Příliš žluťoučký <kůň>");
    }

    [Fact]
    public async Task Returns_an_empty_result_without_a_request_when_there_are_no_events()
    {
        var fixture = new ClientFixture();

        var result = await fixture.Client.SendAsync(Array.Empty<InngestEvent>());

        result.Ids.ShouldBeEmpty();
        fixture.Handler.Requests.ShouldBeEmpty();
        var warning = fixture.Logger.Collector.GetSnapshot().ShouldHaveSingleItem();
        warning.Level.ShouldBe(LogLevel.Warning);
        warning.Id.Id.ShouldBe(300);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Rejects_an_event_without_a_name(string? name)
    {
        var fixture = new ClientFixture();

        var exception = await Should.ThrowAsync<ArgumentException>(() => fixture.Client.SendAsync(new InngestEvent(name!)));

        exception.Message.ShouldContain("name");
        fixture.Handler.Requests.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("inngest/function.failed")]
    [InlineData("INNGEST/anything")]
    public async Task Rejects_names_reserved_for_inngest(string name)
    {
        var fixture = new ClientFixture();

        var exception = await Should.ThrowAsync<ArgumentException>(() => fixture.Client.SendAsync(new InngestEvent(name)));

        exception.Message.ShouldContain("inngest/");
        fixture.Handler.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task Allows_names_that_merely_contain_the_reserved_prefix()
    {
        var fixture = new ClientFixture();
        fixture.Handler.Respond(HttpStatusCode.OK, ClientFixture.Accepted);

        await fixture.Client.SendAsync(new InngestEvent("app/inngest/thing"));

        fixture.Handler.Requests.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Rejects_a_single_bad_event_before_sending_the_others()
    {
        var fixture = new ClientFixture();

        await Should.ThrowAsync<ArgumentException>(() => fixture.Client.SendAsync(new[] { new InngestEvent("a/ok"), new InngestEvent("") }));

        fixture.Handler.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task Rejects_null_events()
    {
        var fixture = new ClientFixture();

        await Should.ThrowAsync<ArgumentNullException>(() => fixture.Client.SendAsync((InngestEvent)null!));
        await Should.ThrowAsync<ArgumentNullException>(() => fixture.Client.SendAsync((IEnumerable<InngestEvent>)null!));
        await Should.ThrowAsync<ArgumentException>(() => fixture.Client.SendAsync(new InngestEvent[] { null! }));
    }

    [Fact]
    public async Task Fails_before_sending_in_cloud_mode_without_an_event_key()
    {
        var fixture = new ClientFixture(TestSettings.Cloud());

        var exception = await Should.ThrowAsync<InngestException>(() => fixture.Client.SendAsync(new InngestEvent("app/ping")));

        exception.Message.ShouldContain("INNGEST_EVENT_KEY");
        fixture.Handler.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task An_empty_send_in_cloud_mode_without_a_key_is_still_just_empty()
    {
        var fixture = new ClientFixture(TestSettings.Cloud());

        var result = await fixture.Client.SendAsync(Array.Empty<InngestEvent>());

        result.Ids.ShouldBeEmpty();
    }

    [Fact]
    public void Reports_the_app_id_of_the_settings()
    {
        var fixture = new ClientFixture(TestSettings.Create(options => options.AppId = "shop"));

        fixture.Client.AppId.ShouldBe("shop");
    }

    [Fact]
    public async Task Passes_the_cancellation_token_to_the_request()
    {
        var fixture = new ClientFixture();
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(() => fixture.Client.SendAsync(new InngestEvent("app/ping"), cancellation.Token));

        fixture.Handler.Requests.ShouldBeEmpty();
    }
}
