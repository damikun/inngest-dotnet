using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using Inngest.UserContext;
using Microsoft.Extensions.Logging;

namespace Inngest.Tests.UserContext;

// Events sent outside a run: the user the source names is attached, protected, with the allowed claims only.
public sealed class SendTests
{
    private static readonly string[] Allowed = ["sub", "name", "role"];

    private static UserContextApp NewApp(IUserContextSource? source, Action<UserContextOptions>? configure = null) =>
        new(configure: options =>
            {
                options.Claims = Allowed;
                configure?.Invoke(options);
            },
            source: source);

    private static InngestEvent Order(object? user = null, string name = "shop/order.paid") =>
        new(name, new { OrderId = 7 }) { User = user };

    [Fact]
    public async Task The_allowed_claims_of_the_current_user_travel_in_the_events_user_protected()
    {
        await using var app = NewApp(new FixedSource(UserContextApp.Ada()));

        await app.Client.SendAsync(Order(), TestContext.Current.CancellationToken);

        var sent = app.SentEvents().ShouldHaveSingleItem();
        var user = sent.GetProperty("user");
        user.EnumerateObject().Select(property => property.Name).ShouldBe([UserContextCodec.Key]);
        user.GetProperty(UserContextCodec.Key).GetProperty("v").GetInt32().ShouldBe(1);
        var payload = app.PayloadOf(user);
        payload.GetProperty("ev").GetString().ShouldBe("shop/order.paid");
        payload.GetProperty("iat").GetInt64().ShouldBe(app.Time.GetUtcNow().ToUnixTimeSeconds());
        payload.GetProperty("exp").GetInt64().ShouldBe(app.Time.GetUtcNow().AddDays(7).ToUnixTimeSeconds());
        payload.GetProperty("c").EnumerateObject().Select(property => property.Name).ShouldBe(["sub", "name", "role"]);
        payload.GetProperty("c").GetProperty("role").EnumerateArray().Select(role => role.GetString()).ShouldBe(["admin", "support"]);
    }

    [Fact]
    public async Task Claims_that_are_not_allowed_never_reach_the_request_to_inngest()
    {
        await using var app = NewApp(new FixedSource(UserContextApp.Ada()));

        await app.Client.SendAsync(Order(), TestContext.Current.CancellationToken);

        var body = app.Http.Single.BodyText;
        body.ShouldNotContain("hunter2");
        body.ShouldNotContain("password");
        body.ShouldNotContain("ada@example.com");
        body.ShouldNotContain("u-42");
        body.ShouldNotContain("Ada");
    }

    [Fact]
    public async Task The_rest_of_the_events_user_is_kept_whatever_type_it_is()
    {
        await using var app = NewApp(new FixedSource(UserContextApp.Ada()));
        var users = new object[]
        {
            new { external_id = "u-42", plan = "pro" },
            new Dictionary<string, object?> { ["external_id"] = "u-42", ["plan"] = "pro" },
            JsonDocument.Parse("""{"external_id":"u-42","plan":"pro"}""").RootElement,
            new JsonObject { ["external_id"] = "u-42", ["plan"] = "pro" },
        };

        await app.Client.SendAsync(users.Select(user => Order(user)), TestContext.Current.CancellationToken);

        var events = app.SentEvents();
        events.Length.ShouldBe(4);
        foreach (var user in events.Select(@event => @event.GetProperty("user")))
        {
            user.GetProperty("external_id").GetString().ShouldBe("u-42");
            user.GetProperty("plan").GetString().ShouldBe("pro");
            user.TryGetProperty(UserContextCodec.Key, out _).ShouldBeTrue();
        }
    }

    [Fact]
    public async Task A_reserved_key_the_app_filled_in_is_replaced_by_the_real_payload()
    {
        await using var app = NewApp(new FixedSource(UserContextApp.Ada()));
        var forged = JsonDocument.Parse(UserContextApp.Envelope("forged-by-a-client")).RootElement.Clone();

        await app.Client.SendAsync(Order(forged), TestContext.Current.CancellationToken);

        var user = app.SentEvents().Single().GetProperty("user");
        user.GetProperty(UserContextCodec.Key).GetProperty("t").GetString().ShouldNotBe("forged-by-a-client");
        app.PayloadOf(user).GetProperty("c").GetProperty("sub")[0].GetString().ShouldBe("u-42");
    }

    [Fact]
    public async Task Events_are_sent_without_a_user_when_the_source_has_none()
    {
        var sources = new IUserContextSource[]
        {
            new FixedSource(null),
            new FixedSource(UserContextApp.Principal(null, new Claim("sub", "u-1"))),
            new FixedSource(UserContextApp.Principal("Cookies", new Claim("password", "x"))),
        };

        foreach (var source in sources)
        {
            await using var app = NewApp(source);

            await app.Client.SendAsync(Order(new { external_id = "u-1" }), TestContext.Current.CancellationToken);

            var user = app.SentEvents().Single().GetProperty("user");
            user.EnumerateObject().Select(property => property.Name).ShouldBe(["external_id"]);
        }
    }

    [Fact]
    public async Task Events_are_sent_as_they_are_without_a_source()
    {
        await using var app = NewApp(source: null);

        await app.Client.SendAsync(Order(), TestContext.Current.CancellationToken);

        app.SentEvents().Single().TryGetProperty("user", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task Every_event_gets_a_payload_made_for_its_own_name()
    {
        await using var app = NewApp(new FixedSource(UserContextApp.Ada()));

        await app.Client.SendAsync([Order(name: "shop/order.paid"), Order(name: "shop/order.shipped"), Order(name: "shop/order.paid")], TestContext.Current.CancellationToken);

        var events = app.SentEvents();
        events.Select(@event => app.PayloadOf(@event.GetProperty("user")).GetProperty("ev").GetString()).ShouldBe(["shop/order.paid", "shop/order.shipped", "shop/order.paid"]);
    }

    [Fact]
    public async Task The_filter_decides_which_events_carry_the_user()
    {
        await using var app = NewApp(new FixedSource(UserContextApp.Ada()), options => options.EventFilter = @event => !@event.Name.StartsWith("audit/", StringComparison.Ordinal));

        await app.Client.SendAsync([Order(name: "shop/order.paid"), Order(name: "audit/seen")], TestContext.Current.CancellationToken);

        var events = app.SentEvents();
        events[0].TryGetProperty("user", out _).ShouldBeTrue();
        events[1].TryGetProperty("user", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task Claims_over_the_size_bound_leave_the_event_without_a_user_and_a_warning_without_the_claims()
    {
        await using var app = NewApp(new FixedSource(UserContextApp.Ada()), options => options.MaxSize = 64);

        await app.Client.SendAsync(Order(), TestContext.Current.CancellationToken);

        app.SentEvents().Single().TryGetProperty("user", out _).ShouldBeFalse();
        var warning = app.Logs.GetSnapshot().Where(record => record.Level == LogLevel.Warning && record.Message.Contains("MaxSize", StringComparison.Ordinal)).ShouldHaveSingleItem();
        warning.Message.ShouldContain("shop/order.paid");
        warning.Message.ShouldNotContain("u-42");
        warning.Message.ShouldNotContain("Ada");
    }

    [Fact]
    public async Task A_user_that_is_not_an_object_is_left_for_the_client_to_refuse()
    {
        await using var app = NewApp(new FixedSource(UserContextApp.Ada()));

        var failure = await Should.ThrowAsync<ArgumentException>(() => app.Client.SendAsync(Order("a string"), TestContext.Current.CancellationToken));

        failure.Message.ShouldContain("user");
        app.Http.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task The_source_is_asked_once_for_each_send()
    {
        var source = new FixedSource(UserContextApp.Ada());
        await using var app = NewApp(source);

        await app.Client.SendAsync([Order(), Order(name: "shop/other")], TestContext.Current.CancellationToken);
        await app.Client.SendAsync(Order(), TestContext.Current.CancellationToken);

        source.Calls.ShouldBe(2);
    }

    internal sealed class FixedSource(ClaimsPrincipal? user) : IUserContextSource
    {
        public int Calls { get; private set; }

        public ValueTask<ClaimsPrincipal?> GetUserAsync(CancellationToken cancellationToken)
        {
            Calls++;
            return ValueTask.FromResult(user);
        }
    }
}
