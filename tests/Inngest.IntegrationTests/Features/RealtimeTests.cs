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
