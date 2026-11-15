using System.Net;
using System.Text.Json;
using Inngest.IntegrationTests.Functions;
using Inngest.IntegrationTests.Harness;
using Microsoft.Extensions.DependencyInjection;

namespace Inngest.IntegrationTests;

public sealed class SendEventTests(DevServerFixture fixture) : IntegrationTest<DevServerFixture>(fixture)
{
    [Fact(Timeout = 30_000)]
    public async Task SendEvent_step_starts_the_function_that_the_event_triggers()
    {
        var run = await RunToEndAsync("itests/send.parent", new NumberEvent(TestId, 9));

        run.Status.ShouldBe(RunStatus.Completed);
        var sentId = run.OutputAs<SentResult>().Ids.ShouldHaveSingleItem();
        var received = await Api.WaitForRunEndAsync(sentId, Cancellation);
        received.Status.ShouldBe(RunStatus.Completed);
        received.OutputAs<NumberResult>().ShouldBe(new NumberResult(10));
        Observed.Values<int>("received").ShouldBe([10]);
    }

    // Inngest goes on past an event it refuses and keeps the others, so the caller needs to know which went through.
    [Fact(Timeout = 30_000)]
    public async Task A_send_that_Inngest_partly_refuses_says_which_events_it_accepted()
    {
        var client = Fixture.App.Services.GetRequiredService<IInngestClient>();

        var exception = await Should.ThrowAsync<SendEventsException>(client.SendAsync(
            [
                new InngestEvent("itests/unhandled", new { TestId, n = 1 }),
                new InngestEvent("itests/unhandled", new { TestId, n = 2 }) { Timestamp = new DateTimeOffset(1975, 1, 1, 0, 0, 0, TimeSpan.Zero) },
                new InngestEvent("itests/unhandled", new { TestId, n = 3 }),
            ],
            Cancellation));

        exception.Ids.Count.ShouldBe(3);
        exception.Ids[0].ShouldNotBeEmpty();
        exception.Ids[1].ShouldBeEmpty();
        exception.Ids[2].ShouldNotBeEmpty();
        exception.Message.ShouldEndWith("(Inngest accepted 2 of 3 events)");
    }

    // A retry sends the whole list again with the same seed for event ids, so the events Inngest kept the first time get
    // the same ids and start no second runs.
    [Fact(Timeout = 60_000)]
    public async Task A_send_retried_after_a_lost_reply_keeps_the_ids_and_starts_each_run_once()
    {
        var losing = new LosesFirstReply();
        var services = new ServiceCollection();
        services.AddInngest(options =>
        {
            options.AppId = "retrying-sender";
            options.IsDev = true;
            options.DevServerUrl = Api.BaseUrl;
        });
        services.AddHttpClient(InngestServiceCollectionExtensions.HttpClientName).AddHttpMessageHandler(() => losing);
        await using var provider = services.BuildServiceProvider();

        var sent = await provider.GetRequiredService<IInngestClient>().SendAsync(
            [new InngestEvent("itests/send.child", new NumberEvent(TestId, 1)), new InngestEvent("itests/send.child", new NumberEvent(TestId, 2))],
            Cancellation);

        losing.Requests.ShouldBe(2);
        sent.Ids.ShouldBe(losing.LostIds);
        foreach (var id in sent.Ids)
        {
            (await Api.WaitForRunEndAsync(id, Cancellation)).Status.ShouldBe(RunStatus.Completed);
        }

        // Time for a second run to start if there were one.
        await Task.Delay(TimeSpan.FromSeconds(2), Cancellation);
        Observed.Values<int>("received").Order().ShouldBe([1, 2]);
    }

    // Lets the first send reach Inngest and loses its reply.
    private sealed class LosesFirstReply : DelegatingHandler
    {
        public int Requests { get; private set; }

        public IReadOnlyList<string> LostIds { get; private set; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = await base.SendAsync(request, cancellationToken);
            if (++Requests > 1)
            {
                return response;
            }

            using (response)
            {
                using var reply = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
                LostIds = [.. reply.RootElement.GetProperty("ids").EnumerateArray().Select(id => id.GetString()!)];
            }

            return new HttpResponseMessage(HttpStatusCode.BadGateway);
        }
    }
}
