using System.Net;
using Microsoft.Extensions.DependencyInjection;

namespace Inngest.Tests.Client;

// Failures of a send that the TypeScript SDK's client tests pin down and no other test here does.
public sealed class SendFailureTests
{
    [Fact]
    public async Task A_status_outside_the_http_range_is_reported_with_the_text_of_the_response()
    {
        var fixture = new ClientFixture();
        fixture.Handler.RespondAlways((HttpStatusCode)600, "something odd happened", "text/plain");

        var exception = await Should.ThrowAsync<InngestException>(
            fixture.Time.AdvanceUntilCompletedAsync(fixture.Client.SendAsync(new InngestEvent("app/user.created"), TestContext.Current.CancellationToken)));

        exception.StatusCode.ShouldBe(600);
        exception.Message.ShouldBe("Inngest API error: 600 something odd happened");
    }

    // Inngest goes on past a refused event and keeps the others, so a caller that sends the batch again sends those twice.
    [Fact]
    public async Task A_send_that_Inngest_partly_refuses_throws_with_the_ids_of_the_events_it_accepted()
    {
        var fixture = new ClientFixture();
        fixture.Handler.Respond(HttpStatusCode.NotAcceptable, """{"ids":["01HQ0000000000000000000001","","01HQ0000000000000000000003"],"status":406,"error":"timestamp is before Jan 1, 1980"}""");

        var exception = await Should.ThrowAsync<SendEventsException>(fixture.Client.SendAsync(Three(), TestContext.Current.CancellationToken));

        exception.StatusCode.ShouldBe(406);
        exception.Ids.ShouldBe(["01HQ0000000000000000000001", "", "01HQ0000000000000000000003"]);
        exception.Message.ShouldBe("Inngest API error: 406 timestamp is before Jan 1, 1980 (Inngest accepted 2 of 3 events)");
        fixture.Handler.Requests.Count.ShouldBe(1);
    }

    // An event over the size limit stops the events after it, which the reply then has no id for.
    [Fact]
    public async Task A_send_cut_short_by_an_event_that_is_too_large_has_an_empty_id_for_each_event_after_it()
    {
        var fixture = new ClientFixture();
        fixture.Handler.Respond(HttpStatusCode.BadRequest, """{"ids":["01HQ0000000000000000000001"],"status":400,"error":"Event is over the max size"}""");

        var exception = await Should.ThrowAsync<SendEventsException>(fixture.Client.SendAsync(Three(), TestContext.Current.CancellationToken));

        exception.Ids.ShouldBe(["01HQ0000000000000000000001", "", ""]);
        exception.Message.ShouldBe("Inngest API error: 400 Event is over the max size (Inngest accepted 1 of 3 events)");
    }

    // Inngest answers a send of one event it refuses as it answers any other send.
    [Fact]
    public async Task A_single_event_that_Inngest_refuses_throws_with_an_empty_id_for_it()
    {
        var fixture = new ClientFixture();
        const string Refused = """{"ids":[""],"status":406,"error":"timestamp is before Jan 1, 1980"}""";
        fixture.Handler.Respond(HttpStatusCode.NotAcceptable, Refused).Respond(HttpStatusCode.NotAcceptable, Refused);
        var early = new InngestEvent("shop/order.placed") { Timestamp = new DateTimeOffset(1975, 1, 1, 0, 0, 0, TimeSpan.Zero) };

        var exception = await Should.ThrowAsync<SendEventsException>(fixture.Client.SendAsync(early, TestContext.Current.CancellationToken));
        var withOptions = await Should.ThrowAsync<SendEventsException>(
            fixture.Client.SendAsync(early, new SendOptions(), TestContext.Current.CancellationToken));

        exception.Ids.ShouldBe([""]);
        exception.Message.ShouldBe("Inngest API error: 406 timestamp is before Jan 1, 1980 (Inngest accepted 0 of 1 event)");
        withOptions.Ids.ShouldBe([""]);
    }

    [Fact]
    public async Task An_error_with_an_id_for_every_event_says_that_Inngest_still_accepted_them()
    {
        var fixture = new ClientFixture();
        fixture.Handler.RespondAlways(HttpStatusCode.OK, """{"ids":["01HQ0000000000000000000001","01HQ0000000000000000000002","01HQ0000000000000000000003"],"status":200,"error":"partial"}""");

        var exception = await Should.ThrowAsync<SendEventsException>(
            fixture.Time.AdvanceUntilCompletedAsync(fixture.Client.SendAsync(Three(), TestContext.Current.CancellationToken)));

        exception.Message.ShouldBe("Inngest API error: 200 partial (Inngest still accepted all 3 events)");
    }

    // As in the TypeScript SDK, a retry sends the whole list again with the same seed, so the events Inngest kept get the
    // same ids and start no second runs.
    [Fact]
    public async Task A_partly_refused_send_that_is_retried_sends_the_whole_list_again_with_the_same_seed()
    {
        var fixture = new ClientFixture();
        fixture.Handler
            .Respond(HttpStatusCode.InternalServerError, """{"ids":["01HQ0000000000000000000001","","01HQ0000000000000000000003"],"status":500,"error":"could not publish"}""")
            .Respond(HttpStatusCode.OK, """{"ids":["01HQ0000000000000000000001","01HQ0000000000000000000002","01HQ0000000000000000000003"],"status":200}""");

        var result = await fixture.Time.AdvanceUntilCompletedAsync(fixture.Client.SendAsync(Three(), TestContext.Current.CancellationToken));

        result.Ids.ShouldBe(["01HQ0000000000000000000001", "01HQ0000000000000000000002", "01HQ0000000000000000000003"]);
        var requests = fixture.Handler.Requests;
        requests.Count.ShouldBe(2);
        requests[1].BodyText.ShouldBe(requests[0].BodyText);
        requests[1].Header("X-Inngest-Event-Id-Seed").ShouldBe(requests[0].Header("X-Inngest-Event-Id-Seed"));
    }

    [Fact]
    public async Task A_refusal_that_names_no_events_throws_a_plain_InngestException()
    {
        var fixture = new ClientFixture();
        fixture.Handler.Respond(HttpStatusCode.Unauthorized, """{"status":401,"error":"Event key not found"}""");

        var exception = await Should.ThrowAsync<InngestException>(fixture.Client.SendAsync(Three(), TestContext.Current.CancellationToken));

        exception.ShouldNotBeOfType<SendEventsException>();
        exception.Message.ShouldBe("Inngest API error: 401 Event key not found");
    }

    [Fact]
    public async Task Middleware_that_throws_rejects_the_send_before_anything_is_sent()
    {
        var fixture = new ClientFixture();
        var provider = new ServiceCollection()
            .AddSingleton<InngestMiddleware>(new Refusing())
            .BuildServiceProvider();
        var client = new Inngest.Client.InngestClient(
            fixture.Factory,
            fixture.Settings,
            fixture.Time,
            provider.GetRequiredService<IServiceScopeFactory>(),
            fixture.Logger,
            provider.GetRequiredService<IServiceProviderIsService>());

        var exception = await Should.ThrowAsync<InvalidOperationException>(
            client.SendAsync(new InngestEvent("app/user.created"), TestContext.Current.CancellationToken));

        exception.Message.ShouldBe("sending is switched off");
        fixture.Handler.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task Middleware_that_throws_after_the_send_still_rejects_it_although_the_events_arrived()
    {
        var fixture = new ClientFixture();
        fixture.Handler.Respond(HttpStatusCode.OK, ClientFixture.Accepted);
        var provider = new ServiceCollection()
            .AddSingleton<InngestMiddleware>(new FailingAfter())
            .BuildServiceProvider();
        var client = new Inngest.Client.InngestClient(
            fixture.Factory,
            fixture.Settings,
            fixture.Time,
            provider.GetRequiredService<IServiceScopeFactory>(),
            fixture.Logger,
            provider.GetRequiredService<IServiceProviderIsService>());

        await Should.ThrowAsync<InvalidOperationException>(client.SendAsync(new InngestEvent("app/user.created"), TestContext.Current.CancellationToken));

        fixture.Handler.Requests.Count.ShouldBe(1);
    }

    private static InngestEvent[] Three() =>
        [new("shop/order.placed", new { n = 1 }), new("shop/order.placed", new { n = 2 }), new("shop/order.placed", new { n = 3 })];

    private sealed class Refusing : InngestMiddleware
    {
        public override Task<SendEventsResult> WrapSendEventAsync(
            FunctionContext? context,
            IReadOnlyList<InngestEvent> events,
            Func<IReadOnlyList<InngestEvent>, Task<SendEventsResult>> next,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("sending is switched off");
    }

    private sealed class FailingAfter : InngestMiddleware
    {
        public override async Task<SendEventsResult> WrapSendEventAsync(
            FunctionContext? context,
            IReadOnlyList<InngestEvent> events,
            Func<IReadOnlyList<InngestEvent>, Task<SendEventsResult>> next,
            CancellationToken cancellationToken)
        {
            await next(events);
            throw new InvalidOperationException("audit log is down");
        }
    }
}
