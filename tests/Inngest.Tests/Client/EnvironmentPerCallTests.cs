using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Inngest.Tests.Client;

public sealed class EnvironmentPerCallTests
{
    private static readonly InngestEvent Created = new("app/user.created", new { Id = 1 });

    [Fact]
    public async Task Options_send_one_event_to_another_environment()
    {
        var fixture = new ClientFixture(TestSettings.Create(options => options.Environment = "branch-1"));
        fixture.Handler.Respond(HttpStatusCode.OK, ClientFixture.Accepted).Respond(HttpStatusCode.OK, ClientFixture.Accepted);

        await fixture.Client.SendAsync(Created, new SendOptions { Environment = "branch-2" }, TestContext.Current.CancellationToken);
        await fixture.Client.SendAsync(Created, TestContext.Current.CancellationToken);

        fixture.Handler.Requests[0].Header("X-Inngest-Env").ShouldBe("branch-2");
        fixture.Handler.Requests[1].Header("X-Inngest-Env").ShouldBe("branch-1");
    }

    [Fact]
    public async Task Options_send_a_batch_to_another_environment_even_without_one_configured()
    {
        var fixture = new ClientFixture();
        fixture.Handler.Respond(HttpStatusCode.OK, ClientFixture.Accepted);

        var result = await fixture.Client.SendAsync([Created, Created], new SendOptions { Environment = "branch-2" }, TestContext.Current.CancellationToken);

        result.Ids.ShouldBe(["01HQ0000000000000000000001"]);
        var request = fixture.Handler.Single;
        request.Header("X-Inngest-Env").ShouldBe("branch-2");
        request.BodyText.ShouldStartWith("""[{"name":"app/user.created","data":{"id":1},"ts":""");
    }

    [Fact]
    public async Task An_empty_environment_keeps_the_apps()
    {
        var fixture = new ClientFixture(TestSettings.Create(options => options.Environment = "branch-1"));
        fixture.Handler.Respond(HttpStatusCode.OK, ClientFixture.Accepted);

        await fixture.Client.SendAsync(Created, new SendOptions { Environment = "" }, TestContext.Current.CancellationToken);

        fixture.Handler.Single.Header("X-Inngest-Env").ShouldBe("branch-1");
    }

    [Fact]
    public async Task Every_retry_goes_to_the_same_environment_with_the_same_seed()
    {
        var fixture = new ClientFixture();
        fixture.Handler
            .Respond(HttpStatusCode.InternalServerError, """{"status":500}""")
            .Respond(HttpStatusCode.OK, ClientFixture.Accepted);

        await fixture.Time.AdvanceUntilCompletedAsync(
            fixture.Client.SendAsync(Created, new SendOptions { Environment = "branch-2" }, TestContext.Current.CancellationToken));

        fixture.Handler.Requests.Count.ShouldBe(2);
        fixture.Handler.Requests.ShouldAllBe(request => request.Header("X-Inngest-Env") == "branch-2");
        fixture.Handler.Requests[1].Header("X-Inngest-Event-Id-Seed").ShouldBe(fixture.Handler.Requests[0].Header("X-Inngest-Event-Id-Seed"));
    }

    [Fact]
    public async Task Middleware_wraps_a_send_with_options_like_any_other()
    {
        var seen = new List<string>();
        var handler = new FakeHttpHandler().Respond(HttpStatusCode.OK, ClientFixture.Accepted);
        await using var provider = ClientServices.Build(
            handler,
            new FakeTimeProvider(),
            inngest => inngest.AddMiddleware(new Renaming(seen)));

        await provider.GetRequiredService<IInngestClient>().SendAsync(Created, new SendOptions { Environment = "branch-2" }, TestContext.Current.CancellationToken);

        seen.ShouldBe(["app/user.created"]);
        handler.Single.Header("X-Inngest-Env").ShouldBe("branch-2");
        handler.Single.BodyText.ShouldContain("\"name\":\"app/user.renamed\"");
    }

    [Fact]
    public async Task Checks_its_arguments()
    {
        var client = new ClientFixture().Client;

        await Should.ThrowAsync<ArgumentNullException>(() => client.SendAsync(Created, (SendOptions)null!, TestContext.Current.CancellationToken));
        await Should.ThrowAsync<ArgumentNullException>(() => client.SendAsync((IEnumerable<InngestEvent>)null!, new SendOptions(), TestContext.Current.CancellationToken));
    }

    // Users fake IInngestClient; the new members have defaults so that their fakes keep compiling.
    [Fact]
    public async Task A_client_that_only_sends_events_refuses_options_and_signals()
    {
        IInngestClient client = new EventsOnlyClient();

        var send = await Should.ThrowAsync<NotSupportedException>(() => client.SendAsync(Created, new SendOptions(), TestContext.Current.CancellationToken));
        var signal = await Should.ThrowAsync<NotSupportedException>(() => client.SendSignalAsync(new InngestSignal("s"), TestContext.Current.CancellationToken));

        send.Message.ShouldBe("EventsOnlyClient does not implement SendAsync with SendOptions.");
        signal.Message.ShouldBe("EventsOnlyClient does not implement SendSignalAsync.");
    }

    private sealed class Renaming(List<string> seen) : InngestMiddleware
    {
        public override Task<SendEventsResult> WrapSendEventAsync(
            FunctionContext? context,
            IReadOnlyList<InngestEvent> events,
            Func<IReadOnlyList<InngestEvent>, Task<SendEventsResult>> next,
            CancellationToken cancellationToken)
        {
            seen.AddRange(events.Select(@event => @event.Name));
            return next([.. events.Select(@event => @event with { Name = "app/user.renamed" })]);
        }
    }

    private sealed class EventsOnlyClient : IInngestClient
    {
        public string AppId => "fake";

        public Task<SendEventsResult> SendAsync(InngestEvent @event, CancellationToken cancellationToken = default) =>
            Task.FromResult(new SendEventsResult([]));

        public Task<SendEventsResult> SendAsync(IEnumerable<InngestEvent> events, CancellationToken cancellationToken = default) =>
            Task.FromResult(new SendEventsResult([]));
    }
}
