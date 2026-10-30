using System.Text;
using System.Text.Json;
using Inngest.Realtime;
using Inngest.Tests.Execution;
using Microsoft.Extensions.Logging.Abstractions;

namespace Inngest.Tests.Realtime;

public sealed class RealtimeSubscriptionTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private readonly FakeRealtimeApi _api = new();
    private readonly TestTimeProvider _time = new(EngineHost.Now);

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Events_are_read_as_the_HTML_standard_defines_them()
    {
        var text = "data: first\r\ndata:second\n\n: keepalive\n\nevent: x\nid: 7\ndata: third\n\nid: 8\n\n";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(text));

        var events = new List<string>();
        await foreach (var data in ServerSentEvents.ReadDataAsync(stream, _time, TimeSpan.FromSeconds(45), Cancel))
        {
            events.Add(data);
        }

        events.ShouldBe(["first\nsecond", "third"]);
    }

    [Fact]
    public async Task A_stream_that_stays_silent_times_out()
    {
        var feed = new SseFeed();
        using var response = feed.Response();
        var stream = await response.Content.ReadAsStreamAsync(Cancel);
        var reading = ReadAllAsync(ServerSentEvents.ReadDataAsync(stream, _time, TimeSpan.FromSeconds(45), Cancel));

        await _time.TimersCreated(1).WaitAsync(Wait, Cancel);
        _time.Advance(TimeSpan.FromSeconds(46));

        var exception = await Should.ThrowAsync<TimeoutException>(() => reading.WaitAsync(Wait, Cancel));
        exception.Message.ShouldBe("Inngest sent nothing for 45 seconds.");
    }

    [Fact]
    public void Messages_are_read_with_their_data_and_streams_with_the_topic_of_their_start()
    {
        var reader = new RealtimeMessageReader<JsonElement>(static data => data.Clone(), readDataOfEveryKind: true);

        var data = reader.Read("""{"kind":"data","data":{"text":"Analyzing"},"created_at":"2026-10-30T20:41:30.40476938Z","channel":"probe","topic":"status","run_id":"01JRUN"}""")!;
        var start = reader.Read("""{"kind":"datastream-start","data":"s1","channel":"probe","topic":"tokens"}""")!;
        var chunk = reader.Read("""{"kind":"chunk","stream_id":"s1","data":"Hel"}""")!;
        var end = reader.Read("""{"kind":"datastream-end","data":"s1","channel":"probe","topic":"tokens"}""")!;
        var orphan = reader.Read("""{"kind":"chunk","stream_id":"s1","data":"lo"}""");

        data.ShouldBe(data with { Kind = RealtimeMessageKind.Data, Channel = "probe", Topic = "status", RunId = "01JRUN" });
        data.Data.GetProperty("text").GetString().ShouldBe("Analyzing");
        data.CreatedAt.ShouldBe(new DateTimeOffset(2026, 10, 30, 20, 41, 30, TimeSpan.Zero).AddTicks(4047693));
        (start.Kind, start.Topic, start.StreamId).ShouldBe((RealtimeMessageKind.StreamStart, "tokens", "s1"));
        (chunk.Kind, chunk.Channel, chunk.Topic, chunk.StreamId, chunk.Text).ShouldBe((RealtimeMessageKind.StreamChunk, "probe", "tokens", "s1", "Hel"));
        end.Kind.ShouldBe(RealtimeMessageKind.StreamEnd);
        orphan.ShouldBeNull();
    }

    [Fact]
    public async Task A_typed_subscription_reads_the_data_as_its_type()
    {
        var feed = new SseFeed();
        _api.Answer(feed);
        var status = new RealtimeTopic<Progress>("order:7", "status");

        var first = FirstAsync(Realtime().SubscribeAsync(status, Cancel));
        await feed.SendAsync("""{"kind":"data","data":{"text":"Packed","percent":50},"channel":"order:7","topic":"status"}""");

        var message = await first.WaitAsync(Wait, Cancel);
        message.Data.ShouldBe(new Progress("Packed", 50));
    }

    [Fact]
    public async Task A_message_whose_data_is_not_the_topics_type_is_skipped()
    {
        var feed = new SseFeed();
        _api.Answer(feed);
        var status = new RealtimeTopic<Progress>("order:7", "status");

        var first = FirstAsync(Realtime().SubscribeAsync(status, Cancel));
        await feed.SendAsync("""{"kind":"data","data":"not a progress","channel":"order:7","topic":"status"}""");
        await feed.SendAsync("""{"kind":"data","data":{"text":"Packed","percent":50},"channel":"order:7","topic":"status"}""");

        (await first.WaitAsync(Wait, Cancel)).Data.ShouldBe(new Progress("Packed", 50));
    }

    [Fact]
    public async Task A_subscription_that_ends_is_opened_again_with_a_new_token()
    {
        var first = new SseFeed();
        var second = new SseFeed();
        _api.Answer(first);
        _api.Answer(second);
        var received = new List<string>();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Cancel);
        var subscription = Collect(Realtime().SubscribeAsync("order:7", ["status"], stop.Token), received, count: 2);

        await first.SendAsync(Message("A"));
        await Until(() => received.Count == 1);
        _time.Advance(TimeSpan.FromSeconds(10));
        await first.EndAsync();
        await second.SendAsync(Message("B"));

        await subscription.WaitAsync(Wait, Cancel);
        received.ShouldBe(["A", "B"]);
        _api.TokensMinted.ShouldBe(2);
    }

    [Fact]
    public async Task A_failure_of_Inngest_is_retried_after_a_delay()
    {
        var feed = new SseFeed();
        _api.Fail(new InngestException("Failed to subscribe to realtime messages: 503", 503));
        _api.Answer(feed);
        var received = new List<string>();
        var subscription = Collect(Realtime().SubscribeAsync("order:7", ["status"], Cancel), received, count: 1);

        await _time.TimersCreated(1).WaitAsync(Wait, Cancel);
        _time.Advance(TimeSpan.FromSeconds(1));
        await feed.SendAsync(Message("A"));

        await subscription.WaitAsync(Wait, Cancel);
        received.ShouldBe(["A"]);
    }

    [Fact]
    public async Task A_refusal_ends_the_subscription()
    {
        _api.TokenFailure = new InngestException("Failed to get a subscription token for channel \"order:7\": 401 Unauthorized", 401);

        var exception = await Should.ThrowAsync<InngestException>(() => FirstAsync(Realtime().SubscribeAsync("order:7", ["status"], Cancel)));

        exception.StatusCode.ShouldBe(401);
    }

    [Fact]
    public void Arguments_are_checked_when_subscribing_not_when_reading() =>
        Should.Throw<ArgumentException>(() => Realtime().SubscribeAsync("", ["status"], Cancel));

    private InngestRealtime Realtime() =>
        new(_api, new RealtimeSubscriber(_api, _time, NullLogger<RealtimeSubscriber>.Instance), EngineHost.Settings, NullLogger<InngestRealtime>.Instance);

    private static string Message(string text) => $$"""{"kind":"data","data":"{{text}}","channel":"order:7","topic":"status"}""";

    private static async Task<T> FirstAsync<T>(IAsyncEnumerable<T> source)
    {
        await foreach (var item in source)
        {
            return item;
        }

        throw new InvalidOperationException("The subscription ended.");
    }

    private static async Task Collect(IAsyncEnumerable<RealtimeMessage<JsonElement>> source, List<string> received, int count)
    {
        await foreach (var message in source)
        {
            lock (received)
            {
                received.Add(message.Data.GetString()!);
                if (received.Count == count)
                {
                    return;
                }
            }
        }
    }

    private static async Task<List<string>> ReadAllAsync(IAsyncEnumerable<string> source)
    {
        var all = new List<string>();
        await foreach (var item in source)
        {
            all.Add(item);
        }

        return all;
    }

    private static async Task Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Wait;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("The condition was not met in time.");
            }

            await Task.Delay(10, Cancel);
        }
    }

    private sealed record Progress(string Text, int Percent);
}
