using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Inngest.IntegrationTests.Evaluation.Functions;
using Inngest.IntegrationTests.Evaluation.Harness;
using Inngest.IntegrationTests.Harness;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Inngest.IntegrationTests.Features;

// Realtime against the dev server: streams and typed data published from steps, subscribers in .NET, and the relay.
[Collection(EvaluationCollections.Features)]
public sealed class RealtimeTests(EvaluationEnvironment environment) : EvaluationScenario<FeaturesFixture>(environment)
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(30);

    private IInngestRealtime Realtime => Fixture.App.Services.GetRequiredService<IInngestRealtime>();

    [Fact(Timeout = 90_000)]
    public async Task A_stream_from_a_step_reaches_a_dotnet_subscriber_chunk_by_chunk()
    {
        var topic = FeatureFunctions.Tokens(TestId);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Cancellation);
        await using var messages = Realtime.SubscribeAsync(topic.Channel, [topic.Name], stop.Token).GetAsyncEnumerator(stop.Token);
        await WaitUntilSubscribedAsync(messages, () => Realtime.PublishAsync(topic, "ready", Cancellation));

        var run = await RunToEndAsync(FeatureFunctions.Streamed, new RunEvent(TestId));

        run.Status.ShouldBe(RunStatus.Completed);
        run.OutputAs<string>().ShouldBe("Your order is on its way 😀");
        var start = await NextAsync(messages, message => message.Kind == RealtimeMessageKind.StreamStart);
        var text = new StringBuilder();
        var chunks = 0;
        while (await NextAsync(messages, _ => true) is { Kind: not RealtimeMessageKind.StreamEnd } message)
        {
            message.Kind.ShouldBe(RealtimeMessageKind.StreamChunk);
            message.StreamId.ShouldBe(start.StreamId);
            message.Topic.ShouldBe(topic.Name);
            text.Append(message.Text);
            chunks++;
        }

        text.ToString().ShouldBe("Your order is on its way 😀");
        chunks.ShouldBeGreaterThan(1);
        start.RunId.ShouldBe(run.RunId);
    }

    [Fact(Timeout = 90_000)]
    public async Task Typed_data_published_from_a_step_reaches_a_typed_subscriber()
    {
        var topic = FeatureFunctions.Status(TestId);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Cancellation);
        await using var messages = Realtime.SubscribeAsync(topic, stop.Token).GetAsyncEnumerator(stop.Token);
        await WaitUntilSubscribedAsync(messages, () => Realtime.PublishAsync(topic, new Progress("ready", 0), Cancellation));

        var run = await RunToEndAsync(FeatureFunctions.TypedPublish, new RunEvent(TestId));

        run.Status.ShouldBe(RunStatus.Completed);
        var message = await NextAsync(messages, message => message.Kind == RealtimeMessageKind.Data && message.Data?.Percent == 50);
        message.Data.ShouldBe(new Progress("Packed", 50));
        message.RunId.ShouldBe(run.RunId);
    }

    // Inngest relays 1,000 reads of a stream, and a model yields a few characters at a time.
    [Fact(Timeout = 120_000)]
    public async Task A_long_stream_of_small_pieces_reaches_a_subscriber_whole()
    {
        var topic = FeatureFunctions.Tokens(TestId);
        var expected = string.Concat(Enumerable.Range(0, 1500).Select(i => $"t{i};"));
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Cancellation);
        await using var messages = Realtime.SubscribeAsync(topic.Channel, [topic.Name], stop.Token).GetAsyncEnumerator(stop.Token);
        await WaitUntilSubscribedAsync(messages, () => Realtime.PublishAsync(topic, "ready", Cancellation));

        var published = await Realtime.PublishStreamAsync(topic, Tokens(), Cancellation);

        published.ShouldBe(expected);
        var start = await NextAsync(messages, message => message.Kind == RealtimeMessageKind.StreamStart);
        var text = new StringBuilder();
        while (await NextAsync(messages, _ => true) is { Kind: not RealtimeMessageKind.StreamEnd } message)
        {
            if (message.Kind == RealtimeMessageKind.StreamChunk && message.StreamId == start.StreamId)
            {
                text.Append(message.Text);
            }
        }

        text.ToString().ShouldBe(expected);

        static async IAsyncEnumerable<string> Tokens()
        {
            for (var i = 0; i < 1500; i++)
            {
                await Task.Delay(5);
                yield return $"t{i};";
            }
        }
    }

    // Inngest reads 1,000 pieces of a stream, then up to 256 KB more, and then closes the connection. A longer stream
    // must not fail for it, and returns all its text.
    [Fact(Timeout = 120_000)]
    public async Task A_stream_longer_than_Inngest_relays_returns_all_its_text()
    {
        var topic = FeatureFunctions.Tokens(TestId);
        var block = new string('x', 4096);
        var expected = string.Concat(Enumerable.Range(0, 350).Select(Block));
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Cancellation);
        await using var messages = Realtime.SubscribeAsync(topic.Channel, [topic.Name], stop.Token).GetAsyncEnumerator(stop.Token);
        await WaitUntilSubscribedAsync(messages, () => Realtime.PublishAsync(topic, "ready", Cancellation));

        var published = await Realtime.PublishStreamAsync(topic, Blocks(), Cancellation);

        published.ShouldBe(expected);
        var start = await NextAsync(messages, message => message.Kind == RealtimeMessageKind.StreamStart);
        var text = new StringBuilder();
        while (await NextAsync(messages, _ => true) is { Kind: not RealtimeMessageKind.StreamEnd } message)
        {
            if (message.Kind == RealtimeMessageKind.StreamChunk && message.StreamId == start.StreamId)
            {
                text.Append(message.Text);
            }
        }

        text.ToString().ShouldBe(expected[..(1000 * 1024)]);

        string Block(int i) => $"{i:D6}{block}";

        // 1,000 pieces and 300 KB more at once, then the rest as slowly as a model writes.
        async IAsyncEnumerable<string> Blocks()
        {
            for (var i = 0; i < 350; i++)
            {
                await Task.Delay(i < 330 ? 0 : 150);
                yield return Block(i);
            }
        }
    }

    // Inngest keeps a message that parses as JSON as that JSON, so text that reads as JSON must go as a JSON string.
    [Fact(Timeout = 90_000)]
    public async Task Text_that_reads_as_JSON_reaches_a_typed_subscriber_as_text()
    {
        var topic = new RealtimeTopic<string>($"run:{TestId}", "answer");
        string[] texts = ["42", "true", """{"answer":1}""", "\"quoted\"", "Ďakujem, <b>Привет</b> & 'ok'", "done"];
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Cancellation);
        await using var messages = Realtime.SubscribeAsync(topic, stop.Token).GetAsyncEnumerator(stop.Token);
        await WaitUntilSubscribedAsync(messages, () => Realtime.PublishAsync(topic, "ready", Cancellation));

        foreach (var text in texts)
        {
            await Realtime.PublishAsync(topic, text, Cancellation);
        }

        var received = new List<string?>();
        while (received.Count < texts.Length)
        {
            received.Add((await NextAsync(messages, message => message.Data != "ready")).Data);
        }

        received.ShouldBe(texts);
    }

    [Fact(Timeout = 90_000)]
    public async Task A_client_without_keys_subscribes_with_a_token_the_app_minted()
    {
        var topic = FeatureFunctions.Status(TestId);
        var token = await Realtime.GetSubscriptionTokenAsync(topic.Channel, [topic.Name], Cancellation);
        await using var client = TokenClient();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Cancellation);
        var subscription = client.GetRequiredService<IInngestRealtime>().SubscribeAsync<Progress>(token, stop.Token);
        await using var messages = subscription.GetAsyncEnumerator(stop.Token);
        await WaitUntilSubscribedAsync(messages, () => Realtime.PublishAsync(topic, new Progress("ready", 0), Cancellation));

        var run = await RunToEndAsync(FeatureFunctions.TypedPublish, new RunEvent(TestId));

        run.Status.ShouldBe(RunStatus.Completed);
        var message = await NextAsync(messages, message => message.Data?.Percent == 50);
        message.Data.ShouldBe(new Progress("Packed", 50));
        message.RunId.ShouldBe(run.RunId);
    }

    [Fact(Timeout = 90_000)]
    public async Task The_relay_serves_a_subscription_to_the_dev_server_as_server_sent_events()
    {
        var topic = FeatureFunctions.Tokens(TestId);
        await using var relay = await StartRelayAsync(topic);
        using var response = await relay.GetTestClient().GetAsync("/realtime", HttpCompletionOption.ResponseHeadersRead, Cancellation);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(Cancellation));
        var lines = Channel.CreateUnbounded<string>();
        _ = Task.Run(async () =>
        {
            while (await reader.ReadLineAsync(Cancellation) is { } read)
            {
                lines.Writer.TryWrite(read);
            }
        });

        // Inngest delivers only to subscribers already connected, so publish until one message arrives.
        string? line = null;
        for (var attempt = 0; attempt < 50 && line is null; attempt++)
        {
            await Realtime.PublishAsync(topic, new { attempt }, Cancellation);
            line = await NextDataLineAsync(lines.Reader, TimeSpan.FromMilliseconds(500));
        }

        line.ShouldNotBeNull();
        using var message = JsonDocument.Parse(line);
        message.RootElement.GetProperty("kind").GetString().ShouldBe("data");
        message.RootElement.GetProperty("channel").GetString().ShouldBe(topic.Channel);
        message.RootElement.GetProperty("topic").GetString().ShouldBe(topic.Name);
        message.RootElement.GetProperty("data").GetProperty("attempt").ValueKind.ShouldBe(JsonValueKind.Number);
    }

    // Inngest delivers only to subscribers connected when a message is published, so publish a marker until it comes back.
    private static async Task WaitUntilSubscribedAsync<T>(IAsyncEnumerator<RealtimeMessage<T>> messages, Func<Task> publishMarker)
    {
        var next = messages.MoveNextAsync().AsTask();
        for (var attempt = 0; attempt < 50; attempt++)
        {
            await publishMarker();
            if (await Task.WhenAny(next, Task.Delay(500, Cancellation)) == next)
            {
                (await next).ShouldBeTrue();
                return;
            }
        }

        throw new TimeoutException("The subscription did not receive the marker.");
    }

    private static async Task<RealtimeMessage<T>> NextAsync<T>(IAsyncEnumerator<RealtimeMessage<T>> messages, Func<RealtimeMessage<T>, bool> match)
    {
        using var timeout = new CancellationTokenSource(Wait);
        while (await messages.MoveNextAsync().AsTask().WaitAsync(timeout.Token))
        {
            if (match(messages.Current))
            {
                return messages.Current;
            }
        }

        throw new InvalidOperationException("The subscription ended.");
    }

    // The data of the next event within the timeout, skipping comments.
    private static async Task<string?> NextDataLineAsync(ChannelReader<string> lines, TimeSpan timeout)
    {
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Cancellation);
        cancel.CancelAfter(timeout);
        try
        {
            while (await lines.WaitToReadAsync(cancel.Token))
            {
                while (lines.TryRead(out var line))
                {
                    if (line.StartsWith("data: ", StringComparison.Ordinal))
                    {
                        return line["data: ".Length..];
                    }
                }
            }
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested && !Cancellation.IsCancellationRequested)
        {
        }

        return null;
    }

    // Services of a client that has no signing key and is not in dev mode, so all it can do is use a token it is given.
    private static ServiceProvider TokenClient()
    {
        var services = new ServiceCollection();
        services.AddInngest(options =>
        {
            options.AppId = "token-client";
            options.IsDev = false;
        });
        return services.BuildServiceProvider();
    }

    // An app of its own that relays the topic from the fixture's dev server.
    private async Task<WebApplication> StartRelayAsync(RealtimeTopic topic)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddInngest(options =>
        {
            options.AppId = "relay";
            options.IsDev = true;
            options.DevServerUrl = Api.BaseUrl;
        });
        var app = builder.Build();
        app.MapInngestRealtime("/realtime", _ => ValueTask.FromResult<RealtimeSubscription?>(new(topic.Channel, [topic.Name])));
        await app.StartAsync(Cancellation);
        return app;
    }
}
