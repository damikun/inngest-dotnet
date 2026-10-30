using System.Net;
using System.Text.Json;
using Inngest.Events;
using Inngest.Functions;
using Inngest.Testing;
using Inngest.Tests.Client;
using Microsoft.Extensions.DependencyInjection;
using static Inngest.Tests.Testing.TestHosts;

namespace Inngest.Tests.Events;

// Sessions as inngest-js handles them: a run's sessions are those all its events share, and what it sends inherits them.
public sealed class EventSessionTests
{
    [Fact]
    public void A_run_keeps_the_sessions_every_event_shares_with_the_same_id()
    {
        var sessions = EventSessions.OfRun([
            Received(("ticket_id", "t-1"), ("user_id", "u-1"), ("org_id", "o-1")),
            Received(("ticket_id", "t-1"), ("user_id", "u-2"), ("org_id", "o-1")),
        ]);

        sessions.ShouldBe(new Dictionary<string, string> { ["org_id"] = "o-1", ["ticket_id"] = "t-1" });
        sessions.Keys.ShouldBe(["org_id", "ticket_id"]);
    }

    [Fact]
    public void A_run_keeps_the_first_five_keys_in_the_byte_order_of_UTF8()
    {
        // U+10000 sorts after U+FFFD in UTF-8, though not in ordinal UTF-16 order.
        var sessions = EventSessions.OfRun([Received(("f", "6"), ("e", "5"), ("\U00010000", "x"), ("d", "4"), ("�", "y"), ("c", "3"), ("b", "2"), ("a", "1"))]);

        sessions.Keys.ShouldBe(["a", "b", "c", "d", "e"]);
        EventSessions.OfRun([Received(("\U00010000", "x"), ("�", "y"))]).Keys.ShouldBe(["�", "\U00010000"]);
    }

    [Fact]
    public void Events_without_sessions_give_a_run_none() =>
        EventSessions.OfRun([Received(("ticket_id", "t-1")), new InngestEvent<JsonElement> { Name = "a", Data = default }]).ShouldBeEmpty();

    [Fact]
    public void Inngest_keeps_an_events_own_sessions_first_and_drops_those_set_to_null()
    {
        var merged = EventSessions.Merge(new EventMeta
        {
            Sessions = new Dictionary<string, string?> { ["user_id"] = "u-9", ["ticket_id"] = null },
            PropagatedSessions = new Dictionary<string, string> { ["ticket_id"] = "t-1", ["user_id"] = "u-1", ["org_id"] = "o-1" },
        });

        merged.ShouldBe(new Dictionary<string, string> { ["user_id"] = "u-9", ["org_id"] = "o-1" });
    }

    [Fact]
    public async Task The_client_sends_both_layers_and_keeps_null_ids()
    {
        var fixture = new ClientFixture();
        fixture.Handler.Respond(HttpStatusCode.OK, ClientFixture.Accepted);

        await fixture.Client.SendAsync(new InngestEvent("app/reply.sent")
        {
            Meta = new EventMeta
            {
                Sessions = new Dictionary<string, string?> { ["ticket_id"] = "t-1", ["user_id"] = null },
                PropagatedSessions = new Dictionary<string, string> { ["org_id"] = "o-1" },
            },
        });

        fixture.Handler.Single.BodyText.ShouldBe(
            """[{"name":"app/reply.sent","data":{},"ts":1705586504000,"meta":{"sessions":{"ticket_id":"t-1","user_id":null},"propagated_sessions":{"org_id":"o-1"}}}]""");
    }

    [Fact]
    public async Task The_client_refuses_an_empty_session_id()
    {
        var fixture = new ClientFixture();

        var exception = await Should.ThrowAsync<ArgumentException>(() => fixture.Client.SendAsync(new InngestEvent("app/reply.sent")
        {
            Meta = new EventMeta { Sessions = new Dictionary<string, string?> { ["ticket_id"] = "" } },
        }));

        exception.Message.ShouldBe("Session \"ticket_id\" of event \"app/reply.sent\" has an empty id. Use null to drop an inherited session.");
        fixture.Handler.Requests.ShouldBeEmpty();
    }

    [Fact]
    public void A_received_event_reads_its_sessions_with_numeric_ids_as_text()
    {
        using var document = JsonDocument.Parse("""{"name":"a","data":{},"meta":{"sessions":{"ticket_id":"t-1","order":42}}}""");

        var @event = EventReader.Read<JsonElement>(document.RootElement, JsonSerializerOptions.Default);

        @event.Meta!.Sessions.ShouldBe(new Dictionary<string, string?> { ["ticket_id"] = "t-1", ["order"] = "42" });
    }

    [Fact]
    public async Task What_a_run_sends_inherits_its_sessions_unless_told_not_to()
    {
        await using var host = For(async context =>
        {
            await context.Step.SendEventAsync("reply", new InngestEvent("app/reply.sent"));
            await context.Step.SendEventAsync("audit", new InngestEvent("app/audit") { Meta = new EventMeta { InheritSessions = false } });
            await context.Step.RunAsync("notify", async () =>
                await context.Services.GetRequiredService<IInngestClient>().SendAsync(new InngestEvent("app/notified")
                {
                    Meta = new EventMeta { Sessions = new Dictionary<string, string?> { ["ticket_id"] = null, ["channel"] = "email" } },
                }));
            return context.Sessions;
        });

        var run = await host.RunAsync("fn", WithSessions(("ticket_id", "t-1"), ("user_id", "u-1")), Cancel);

        run.Status.ShouldBe(TestRunStatus.Completed);
        run.GetOutput<Dictionary<string, string>>().ShouldBe(new Dictionary<string, string> { ["ticket_id"] = "t-1", ["user_id"] = "u-1" });
        var inherited = new Dictionary<string, string> { ["ticket_id"] = "t-1", ["user_id"] = "u-1" };
        run.SentEvents.Select(@event => @event.Name).ShouldBe(["app/reply.sent", "app/audit", "app/notified"]);
        run.SentEvents[0].Meta!.PropagatedSessions.ShouldBe(inherited);
        run.SentEvents[1].Meta!.PropagatedSessions.ShouldBeNull();
        run.SentEvents[2].Meta!.PropagatedSessions.ShouldBe(inherited);
        EventSessions.Merge(run.SentEvents[2].Meta).ShouldBe(new Dictionary<string, string> { ["channel"] = "email", ["user_id"] = "u-1" });
    }

    [Fact]
    public async Task An_invoked_function_inherits_the_sessions_under_its_own()
    {
        await using var host = For(async context => await context.Step.InvokeAsync<string>("summarize", new InvokeOptions("summarize")
        {
            Meta = new EventMeta { Sessions = new Dictionary<string, string?> { ["user_id"] = null } },
        }));

        var run = await host.RunAsync("fn", WithSessions(("ticket_id", "t-1"), ("user_id", "u-1")), new StepMocks().Invoke("summarize", "done"), Cancel);

        run.GetStep("summarize").Options.GetProperty("payload").GetProperty("meta").GetRawText()
            .ShouldBe("""{"sessions":{"user_id":null},"propagated_sessions":{"ticket_id":"t-1","user_id":"u-1"}}""");
    }

    [Fact]
    public async Task With_propagation_off_a_run_has_no_sessions_and_passes_none_on()
    {
        var services = Services(async context =>
        {
            await context.Step.SendEventAsync("reply", new InngestEvent("app/reply.sent"));
            return context.Sessions.Count;
        });
        services.Configure<InngestOptions>(options => options.SessionPropagation = false);
        await using var host = new InngestTestHost(services);

        var run = await host.RunAsync("fn", WithSessions(("ticket_id", "t-1")), Cancel);

        run.GetOutput<int>().ShouldBe(0);
        run.SentEvents.ShouldHaveSingleItem().Meta.ShouldBeNull();
    }

    private static InngestEvent WithSessions(params (string Key, string Id)[] sessions) =>
        Event() with { Meta = new EventMeta { Sessions = sessions.ToDictionary(session => session.Key, session => (string?)session.Id) } };

    private static InngestEvent<JsonElement> Received(params (string Key, string Id)[] sessions) => new()
    {
        Name = "app/event",
        Data = default,
        Meta = new EventMeta { Sessions = sessions.ToDictionary(session => session.Key, session => (string?)session.Id) },
    };
}
