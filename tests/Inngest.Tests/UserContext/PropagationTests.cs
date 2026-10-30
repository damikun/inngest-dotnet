using System.Security.Claims;
using System.Text.Json;
using Inngest.Functions;
using Inngest.Tests.Execution;
using Inngest.UserContext;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;

namespace Inngest.Tests.UserContext;

// The user of a run on the events and invocations the run sends: the same snapshot, with its original expiry.
public sealed class PropagationTests
{
    private static readonly string[] Allowed = ["sub", "name", "role"];

    private static UserContextApp NewApp(Action<UserContextOptions>? configure = null, IUserContextSource? source = null, IDataProtectionProvider? keys = null) =>
        new(keys,
            options =>
            {
                options.Claims = Allowed;
                configure?.Invoke(options);
            },
            source: source);

    private static Call Event(string user, string name = UserContextApp.EventName) => Call.New().Event(UserContextApp.EventJson(user, name));

    private static FunctionDefinition SendsFromAStep(string name = "shop/notified") =>
        TestFunction.Define(async context =>
        {
            await context.Step.SendEventAsync("notify", [new InngestEvent(name, new { Order = 7 })]);
            return "done";
        });

    private static FunctionDefinition SendsWithTheClient(string name = "shop/notified") =>
        TestFunction.Define(async context =>
        {
            await context.Services.GetRequiredService<IInngestClient>().SendAsync(new InngestEvent(name, new { Order = 7 }));
            return "done";
        });

    [Fact]
    public async Task An_event_sent_by_a_step_carries_the_user_of_the_run_with_its_original_expiry()
    {
        await using var app = NewApp();
        var user = app.Seal(UserContextApp.Ada());
        var original = app.PayloadOf(JsonDocument.Parse(user).RootElement);
        app.Time.Advance(TimeSpan.FromDays(2));

        var result = await app.CallAsync(SendsFromAStep(), Event(user), runStart: app.Time.GetUtcNow());

        result.OpCodes.ShouldBe(["StepRun"]);
        var sent = app.SentEvents().ShouldHaveSingleItem();
        var payload = app.PayloadOf(sent.GetProperty("user"));
        payload.GetProperty("ev").GetString().ShouldBe("shop/notified");
        payload.GetProperty("iat").GetInt64().ShouldBe(original.GetProperty("iat").GetInt64());
        payload.GetProperty("exp").GetInt64().ShouldBe(original.GetProperty("exp").GetInt64());
        payload.GetProperty("c").GetRawText().ShouldBe(original.GetProperty("c").GetRawText());
    }

    [Fact]
    public async Task An_event_sent_with_the_client_inside_a_run_carries_the_users_of_the_run_and_never_asks_the_source()
    {
        var other = new SendTests.FixedSource(UserContextApp.Principal("Cookies", new Claim("sub", "someone-else")));
        await using var app = NewApp(source: other);

        await app.CallAsync(SendsWithTheClient(), Event(app.Seal(UserContextApp.Ada())), runStart: app.Time.GetUtcNow());

        var payload = app.PayloadOf(app.SentEvents().Single().GetProperty("user"));
        payload.GetProperty("c").GetProperty("sub")[0].GetString().ShouldBe("u-42");
        other.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task A_run_without_a_user_sends_events_without_one_and_does_not_ask_the_source_either()
    {
        var other = new SendTests.FixedSource(UserContextApp.Ada());
        await using var app = NewApp(source: other);

        await app.CallAsync(SendsFromAStep(), Event("{}"));
        await app.CallAsync(SendsWithTheClient(), Event("{}"));

        app.SentEvents(0).Single().TryGetProperty("user", out _).ShouldBeFalse();
        app.SentEvents(1).Single().TryGetProperty("user", out _).ShouldBeFalse();
        other.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task Events_of_a_run_carry_no_user_when_propagation_is_off()
    {
        await using var app = NewApp(options => options.Propagate = false);
        var user = Event(app.Seal(UserContextApp.Ada()));

        await app.CallAsync(SendsFromAStep(), user, runStart: app.Time.GetUtcNow());
        await app.CallAsync(SendsWithTheClient(), user, runStart: app.Time.GetUtcNow());

        app.SentEvents(0).Single().TryGetProperty("user", out _).ShouldBeFalse();
        app.SentEvents(1).Single().TryGetProperty("user", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task The_filter_applies_to_the_events_a_run_sends()
    {
        await using var app = NewApp(options => options.EventFilter = @event => @event.Name != "shop/quiet");
        var user = Event(app.Seal(UserContextApp.Ada()));

        await app.CallAsync(SendsFromAStep("shop/notified"), user, runStart: app.Time.GetUtcNow());
        await app.CallAsync(SendsFromAStep("shop/quiet"), user, runStart: app.Time.GetUtcNow());

        app.SentEvents(0).Single().TryGetProperty("user", out _).ShouldBeTrue();
        app.SentEvents(1).Single().TryGetProperty("user", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task The_rest_of_the_user_of_an_event_a_run_sends_is_kept()
    {
        await using var app = NewApp();
        var function = TestFunction.Define(async context =>
        {
            await context.Step.SendEventAsync("notify", [new InngestEvent("shop/notified", new { }) { User = new { external_id = "u-42" } }]);
            return "done";
        });

        await app.CallAsync(function, Event(app.Seal(UserContextApp.Ada())), runStart: app.Time.GetUtcNow());

        var user = app.SentEvents().Single().GetProperty("user");
        user.GetProperty("external_id").GetString().ShouldBe("u-42");
        user.TryGetProperty(UserContextCodec.Key, out _).ShouldBeTrue();
    }

    [Fact]
    public async Task The_user_follows_the_events_from_run_to_run_and_the_expiry_stays_that_of_the_first_send()
    {
        var keys = new EphemeralDataProtectionProvider();
        await using var sender = NewApp(keys: keys);
        await using var worker = NewApp(keys: keys);
        var first = sender.Seal(UserContextApp.Ada());
        var firstPayload = sender.PayloadOf(JsonDocument.Parse(first).RootElement);

        // Run one, started by the event the request sent, sends the next event.
        await worker.CallAsync(SendsFromAStep("shop/second"), Event(first), runStart: worker.Time.GetUtcNow());
        var second = worker.SentEvents().Single().GetProperty("user").GetRawText();
        worker.Time.Advance(TimeSpan.FromDays(3));

        // Run two, started by that event, sends one more.
        await worker.CallAsync(SendsFromAStep("shop/third"), Event(second, "shop/second"), runStart: worker.Time.GetUtcNow());
        var third = worker.SentEvents(1).Single().GetProperty("user").GetRawText();
        var seen = await worker.CallAsync(UserContextApp.Observe(), Event(third, "shop/third"), runStart: worker.Time.GetUtcNow());

        UserContextApp.SeenIn(seen).Claims.ShouldBe(["sub=u-42", "name=Ada Lovelace", "role=admin", "role=support"]);
        worker.PayloadOf(JsonDocument.Parse(third).RootElement).GetProperty("exp").GetInt64().ShouldBe(firstPayload.GetProperty("exp").GetInt64());
    }

    [Fact]
    public async Task A_chain_of_runs_cannot_outlive_the_expiry_of_the_first_send()
    {
        await using var app = NewApp(options => options.Lifetime = TimeSpan.FromDays(1));
        await app.CallAsync(SendsFromAStep("shop/second"), Event(app.Seal(UserContextApp.Ada())), runStart: app.Time.GetUtcNow());
        var second = app.SentEvents().Single().GetProperty("user").GetRawText();
        app.Time.Advance(TimeSpan.FromDays(2));

        var seen = await app.CallAsync(UserContextApp.Observe(), Event(second, "shop/second"), runStart: app.Time.GetUtcNow());

        UserContextApp.SeenIn(seen).IsAuthenticated.ShouldBeFalse();
    }

    [Fact]
    public async Task A_payload_taken_from_one_event_cannot_be_put_on_another()
    {
        await using var app = NewApp();
        var user = app.Seal(UserContextApp.Ada(), "shop/order.paid");

        var seen = await app.CallAsync(UserContextApp.Observe(), Event(user, "shop/account.deleted"), runStart: app.Time.GetUtcNow());

        UserContextApp.SeenIn(seen).IsAuthenticated.ShouldBeFalse();
    }

    [Fact]
    public async Task An_invocation_carries_the_user_when_the_code_asks_and_the_invoked_run_restores_it()
    {
        await using var app = NewApp();
        var original = app.Seal(UserContextApp.Ada());
        var function = TestFunction.Define(async context =>
        {
            var options = new InvokeOptions("child") { Data = new { Order = 7 }, User = new { external_id = "u-42" } };
            return await context.Step.InvokeAsync<string>("call-child", options.WithUser(context.Services.GetRequiredService<ICurrentUser>()));
        });

        var parent = await app.CallAsync(function, Event(original), runStart: app.Time.GetUtcNow());

        parent.OpCodes.ShouldBe(["InvokeFunction"]);
        var invoked = parent.Op().GetProperty("opts").GetProperty("payload").GetProperty("user");
        invoked.GetProperty("external_id").GetString().ShouldBe("u-42");
        app.PayloadOf(invoked).GetProperty("ev").GetString().ShouldBe(SystemEvents.FunctionInvoked);
        app.PayloadOf(invoked).GetProperty("exp").GetInt64().ShouldBe(app.PayloadOf(JsonDocument.Parse(original).RootElement).GetProperty("exp").GetInt64());

        var child = await app.CallAsync(
            UserContextApp.Observe(),
            Call.New().Event($$"""{"name":"inngest/function.invoked","id":"01JINVOKED","ts":1793268000000,"data":{"order":7},"user":{{invoked.GetRawText()}}}"""),
            app.Time.GetUtcNow());

        var seen = UserContextApp.SeenIn(child);
        seen.IsAuthenticated.ShouldBeTrue();
        seen.Claims.ShouldBe(["sub=u-42", "name=Ada Lovelace", "role=admin", "role=support"]);
    }

    [Fact]
    public async Task An_invocation_does_not_carry_the_user_unless_the_code_asks()
    {
        await using var app = NewApp();
        var function = TestFunction.Define(async context =>
            await context.Step.InvokeAsync<string>("call-child", new InvokeOptions("child") { Data = new { Order = 7 } }));

        var parent = await app.CallAsync(function, Event(app.Seal(UserContextApp.Ada())), runStart: app.Time.GetUtcNow());

        parent.Op().GetProperty("opts").GetProperty("payload").TryGetProperty("user", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task Asking_for_the_user_of_a_run_that_has_none_or_for_a_fake_leaves_the_invocation_alone()
    {
        await using var app = NewApp();
        var plain = new InvokeOptions("child") { Data = new { Order = 7 }, User = new { external_id = "u-42" } };
        var anonymous = TestFunction.Define(context =>
        {
            context.Services.GetRequiredService<ICurrentUser>().IsAuthenticated.ShouldBeFalse();
            return Task.FromResult<object?>(plain.WithUser(context.Services.GetRequiredService<ICurrentUser>()));
        });

        await app.CallAsync(anonymous, Event("{}"));

        plain.WithUser(new FakeUser()).ShouldBeSameAs(plain);
        Should.Throw<ArgumentNullException>(() => plain.WithUser(null!));
        Should.Throw<ArgumentNullException>(() => ((InvokeOptions)null!).WithUser(new FakeUser()));
    }

    private sealed class FakeUser : ICurrentUser
    {
        public ClaimsPrincipal Principal { get; } = new(new ClaimsIdentity([new Claim("sub", "fake")], "Test"));

        public bool IsAuthenticated => true;

        public DateTimeOffset? IssuedAt => null;
    }
}
