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
