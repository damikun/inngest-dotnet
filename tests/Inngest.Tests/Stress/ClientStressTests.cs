using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using Inngest.Client;
using Inngest.Tests.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Inngest.Tests.Stress;

// Many sends at once through one client, a third of them failing on their first attempt. Each send must post only its
// own events, retry with the same body and seed, and get its own ids back.
public sealed class ClientStressTests
{
    private const int Sends = 1000;

    [Fact]
    public async Task Concurrent_sends_post_their_own_events_and_keep_their_body_and_seed_across_retries()
    {
        var api = new FlakyEventApi();
        using var provider = new ServiceCollection().BuildServiceProvider();
        var client = new InngestClient(
            new FakeHttpClientFactory(api),
            TestSettings.Create(),
            new ImmediateTimeProvider(),
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<InngestClient>.Instance,
            provider.GetRequiredService<IServiceProviderIsService>());

        var results = await Task.WhenAll(Enumerable.Range(0, Sends).Select(send => Task.Run(() =>
            client.SendAsync(
                [.. Enumerable.Range(0, 1 + (send % 3)).Select(part => new InngestEvent("test/stressed", new { send, part }))],
                TestContext.Current.CancellationToken))));

        for (var send = 0; send < Sends; send++)
        {
            results[send].Ids.ShouldBe([.. Enumerable.Range(0, 1 + (send % 3)).Select(part => $"{send}-{part}")]);
            var attempts = api.Attempts[send];
            attempts.Count.ShouldBe(send % 3 == 0 ? 2 : 1);
            attempts.Select(static attempt => attempt.Seed).Distinct().ShouldHaveSingleItem();
            attempts.Select(static attempt => attempt.Body).Distinct().ShouldHaveSingleItem();
        }

        api.Attempts.Values.Select(static attempts => attempts[0].Seed).ShouldBeUnique();
    }

    // Fails the first attempt of every third send, alternating between a 500 and a dropped connection, and answers
    // every other attempt with ids made from the events in the body.
    private sealed class FlakyEventApi : HttpMessageHandler
    {
        public ConcurrentDictionary<int, List<(string Seed, string Body)>> Attempts { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            using var events = JsonDocument.Parse(body);
            var send = events.RootElement[0].GetProperty("data").GetProperty("send").GetInt32();
            var attempts = Attempts.GetOrAdd(send, static _ => []);
            int attempt;
            lock (attempts)
            {
                attempts.Add((request.Headers.GetValues("X-Inngest-Event-Id-Seed").Single(), body));
                attempt = attempts.Count;
            }

            if (send % 3 == 0 && attempt == 1)
            {
                return send % 2 == 0
                    ? new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("""{"error":"try again","status":500}""") }
                    : throw new HttpRequestException("The connection was reset.");
            }

            var ids = events.RootElement.EnumerateArray().Select(static e =>
                $"\"{e.GetProperty("data").GetProperty("send").GetInt32()}-{e.GetProperty("data").GetProperty("part").GetInt32()}\"");
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($$"""{"ids":[{{string.Join(',', ids)}}],"status":200}""", Encoding.UTF8, "application/json"),
            };
        }
    }

    // Lets the retry backoff pass at once, so that a thousand retries take no time.
    private sealed class ImmediateTimeProvider : TimeProvider
    {
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            TimeProvider.System.CreateTimer(callback, state, TimeSpan.Zero, period);
    }
}
