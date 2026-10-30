using System.Text.Json;
using System.Threading.Channels;
using DotNet.Testcontainers.Builders;
using Inngest.IntegrationTests.Evaluation.Functions;
using Inngest.IntegrationTests.Evaluation.Harness;
using Inngest.IntegrationTests.Harness;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace Inngest.IntegrationTests.Features;

// The SignalR bridge against the dev server: real SignalR clients over WebSockets to apps on Kestrel, whose subscriptions
// to Inngest are the real ones, and a Redis backplane between two instances.
public sealed class SignalRTests(EvaluationEnvironment environment) : EvaluationScenario<FeaturesFixture>(environment)
{
    private static readonly TimeSpan MessageTimeout = TimeSpan.FromSeconds(30);

    private IInngestRealtime Realtime => Fixture.App.Services.GetRequiredService<IInngestRealtime>();

    [Fact(Timeout = 90_000)]
    public async Task A_function_publishes_and_a_SignalR_client_receives_the_message_once()
    {
        var topic = FeatureFunctions.Status(TestId);
        await using var app = await StartHubAsync();
        await using var client = new Client(app);
        await client.StartAsync();
        await client.Connection.InvokeAsync("Subscribe", topic.Channel, new[] { topic.Name }, Cancellation);
        await WaitUntilSubscribedAsync(topic, client);

        var run = await RunToEndAsync(FeatureFunctions.TypedPublish, new RunEvent(TestId));

        run.Status.ShouldBe(RunStatus.Completed);
        using var published = await client.NextAsync(IsPacked);
        published.RootElement.GetProperty("kind").GetString().ShouldBe("data");
        published.RootElement.GetProperty("channel").GetString().ShouldBe(topic.Channel);
        published.RootElement.GetProperty("topic").GetString().ShouldBe(topic.Name);
        published.RootElement.GetProperty("run_id").GetString().ShouldBe(run.RunId);
        published.RootElement.GetProperty("data").GetProperty("text").GetString().ShouldBe("Packed");

        // Nothing else arrives for it, not a second copy.
        await Task.Delay(TimeSpan.FromSeconds(1.5), Cancellation);
        client.Count(IsPacked).ShouldBe(1);
    }

    [Fact(Timeout = 120_000)]
    public async Task Instances_on_a_Redis_backplane_deliver_each_message_once_to_each_client()
    {
        var topic = FeatureFunctions.Status(TestId);
        await using var redis = new ContainerBuilder("redis:7")
            .WithPortBinding(6379, assignRandomHostPort: true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("Ready to accept connections"))
            .Build();
        await redis.StartAsync(Cancellation);
        var endpoint = $"{redis.Hostname}:{redis.GetMappedPublicPort(6379)}";

        // The same hub on the same backplane twice, as two instances of one app. Each has a subscription to Inngest of its own.
        await using var first = await StartHubAsync(signalR => signalR.AddStackExchangeRedis(endpoint, options => options.Configuration.ChannelPrefix = RedisChannel.Literal(TestId)));
        await using var second = await StartHubAsync(signalR => signalR.AddStackExchangeRedis(endpoint, options => options.Configuration.ChannelPrefix = RedisChannel.Literal(TestId)));
        await using var one = new Client(first);
        await using var two = new Client(second);
        await using var three = new Client(second);
        await one.StartAsync();
        await two.StartAsync();
        await three.StartAsync();
        foreach (var client in new[] { one, two, three })
        {
            await client.Connection.InvokeAsync("Subscribe", topic.Channel, new[] { topic.Name }, Cancellation);
        }

        await WaitUntilSubscribedAsync(topic, one, two, three);
        for (var number = 0; number < 5; number++)
        {
            await Realtime.PublishAsync(topic, new Progress("Final", number), Cancellation);
        }

        foreach (var client in new[] { one, two, three })
        {
            await client.WaitForCountAsync(message => message.RootElement.GetProperty("data").GetProperty("text").GetString() == "Final", 5);
        }

        // Neither instance sends to the clients of the other, as a message sent to a group on a backplane would.
        await Task.Delay(TimeSpan.FromSeconds(1.5), Cancellation);
        foreach (var client in new[] { one, two, three })
        {
            client.Numbers("Final").ShouldBe([0, 1, 2, 3, 4]);
        }
    }

    private static bool IsPacked(JsonDocument message) => message.RootElement.GetProperty("data").GetProperty("text").GetString() == "Packed";

    // Inngest delivers only to subscribers connected when a message is published, so publish a marker until every client has one.
    private async Task WaitUntilSubscribedAsync(RealtimeTopic<Progress> topic, params Client[] clients)
    {
        for (var attempt = 0; attempt < 60; attempt++)
        {
            await Realtime.PublishAsync(topic, new Progress("Marker", attempt), Cancellation);
            await Task.Delay(500, Cancellation);
            if (clients.All(client => client.Count(message => message.RootElement.GetProperty("data").GetProperty("text").GetString() == "Marker") > 0))
            {
                return;
            }
        }

        throw new TimeoutException("A client did not receive the marker.");
    }

    // An app of its own on Kestrel whose bridge subscribes to the fixture's dev server.
    private async Task<WebApplication> StartHubAsync(Action<ISignalRServerBuilder>? configureSignalR = null)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        var signalR = builder.Services.AddSignalR();
        configureSignalR?.Invoke(signalR);
        builder.Services.AddInngest(options =>
        {
            options.AppId = "signalr";
            options.IsDev = true;
            options.DevServerUrl = Api.BaseUrl;
        });
        builder.Services.AddInngestSignalR(options => options.Authorize = request => ValueTask.FromResult<RealtimeSubscription?>(request.Requested));
        var app = builder.Build();
        app.MapInngestRealtimeHub("/hub");
        await app.StartAsync(Cancellation);
        return app;
    }

    // A SignalR client that keeps the JSON of every message it is sent.
    private sealed class Client : IAsyncDisposable
    {
        private readonly List<JsonDocument> _messages = [];
        private readonly Channel<JsonDocument> _arrivals = Channel.CreateUnbounded<JsonDocument>();

        public Client(WebApplication app)
        {
            Connection = new HubConnectionBuilder()
                .WithUrl(new Uri(new Uri(app.Urls.First()), "/hub"), options => options.Transports = HttpTransportType.WebSockets)
                .Build();
            Connection.On<string>("Realtime", json =>
            {
                var message = JsonDocument.Parse(json);
                lock (_messages)
                {
                    _messages.Add(message);
                }

                _arrivals.Writer.TryWrite(message);
            });
        }

        public HubConnection Connection { get; }

        public Task StartAsync() => Connection.StartAsync(Cancellation);

        public int Count(Func<JsonDocument, bool> match)
        {
            lock (_messages)
            {
                return _messages.Count(message => Matches(message, match));
            }
        }

        // The numbers of the percent of the messages with this text, in the order they arrived.
        public int[] Numbers(string text)
        {
            lock (_messages)
            {
                return [.. _messages
                    .Where(message => Matches(message, candidate => candidate.RootElement.GetProperty("data").GetProperty("text").GetString() == text))
                    .Select(message => message.RootElement.GetProperty("data").GetProperty("percent").GetInt32())];
            }
        }

        public async Task WaitForCountAsync(Func<JsonDocument, bool> match, int count)
        {
            using var timeout = new CancellationTokenSource(MessageTimeout);
            while (Count(match) < count)
            {
                await Task.Delay(50, timeout.Token);
            }
        }

        // The next message that matches; the document is the caller's to dispose.
        public async Task<JsonDocument> NextAsync(Func<JsonDocument, bool> match)
        {
            using var timeout = new CancellationTokenSource(MessageTimeout);
            while (true)
            {
                var message = await _arrivals.Reader.ReadAsync(timeout.Token);
                if (Matches(message, match))
                {
                    return JsonDocument.Parse(message.RootElement.GetRawText());
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            await Connection.DisposeAsync();
            lock (_messages)
            {
                foreach (var message in _messages)
                {
                    message.Dispose();
                }
            }
        }

        // Messages without a number, such as an error, never match.
        private static bool Matches(JsonDocument message, Func<JsonDocument, bool> match) =>
            message.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object && match(message);
    }
}
