using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Time.Testing;
using Inngest.Tests.Realtime;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace Inngest.Tests.Blazor;

// The source WebAssembly uses: the server-sent events of a MapInngestRealtime endpoint, read through an HttpClient whose
// handler the test plays, and fake time for the delays between connections.
public sealed class RelayRealtimeSourceTests : IDisposable
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private readonly FakeRelayHandler _relay = new();
    private readonly RecordingTimeProvider _time = new(new DateTimeOffset(2026, 10, 30, 10, 0, 0, TimeSpan.Zero));
    private readonly List<ServiceProvider> _providers = [];

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        foreach (var provider in _providers)
        {
            provider.Dispose();
        }
    }

    [Fact]
    public async Task The_events_of_the_relay_become_messages_with_their_channel_topic_and_data()
    {
        var feed = new SseFeed();
        _relay.Answer(feed);
        var received = Collect(Source().SubscribeAsync("order:7", ["status", "tokens"], Cancel), count: 5);

        await feed.WriteAsync(":\n\n");
        await feed.SendAsync("""{"kind":"data","data":{"text":"Packed","percent":50},"created_at":"2026-10-30T20:41:30.40476938Z","channel":"order:7","topic":"status","run_id":"01JRUN"}""");
        await feed.SendAsync("""{"kind":"datastream-start","data":"s1","channel":"order:7","topic":"tokens"}""");
        await feed.SendAsync("""{"kind":"chunk","stream_id":"s1","data":"Hel"}""");
        await feed.SendAsync("""{"kind":"chunk","stream_id":"s1","data":"lo"}""");
        await feed.SendAsync("""{"kind":"datastream-end","data":"s1","channel":"order:7","topic":"tokens"}""");

        var messages = await received.WaitAsync(Wait, Cancel);
        messages.Select(message => (message.Kind, message.Channel, message.Topic)).ShouldBe(
        [
            (RealtimeMessageKind.Data, "order:7", "status"),
            (RealtimeMessageKind.StreamStart, "order:7", "tokens"),
            (RealtimeMessageKind.StreamChunk, "order:7", "tokens"),
            (RealtimeMessageKind.StreamChunk, "order:7", "tokens"),
            (RealtimeMessageKind.StreamEnd, "order:7", "tokens"),
        ]);
        messages[0].Data.GetProperty("text").GetString().ShouldBe("Packed");
        messages[0].RunId.ShouldBe("01JRUN");
        messages[0].CreatedAt.ShouldBe(new DateTimeOffset(2026, 10, 30, 20, 41, 30, TimeSpan.Zero).AddTicks(4047693));
        messages.Skip(1).Select(message => message.StreamId).ShouldAllBe(id => id == "s1");
        messages.Skip(2).Take(2).Select(message => message.Text).ShouldBe(["Hel", "lo"]);
    }

    [Fact]
    public async Task Messages_of_topics_that_were_not_asked_for_are_dropped()
    {
        var feed = new SseFeed();
        _relay.Answer(feed);
        var received = Collect(Source().SubscribeAsync("order:7", ["status"], Cancel), count: 2);

        await feed.SendAsync("""{"kind":"data","data":"secret","channel":"order:7","topic":"audit"}""");
        await feed.SendAsync("""{"kind":"datastream-start","data":"s1","channel":"order:7","topic":"audit"}""");
        await feed.SendAsync("""{"kind":"chunk","stream_id":"s1","data":"secret"}""");
        await feed.SendAsync("""{"kind":"data","data":"first","channel":"order:7","topic":"status"}""");
        await feed.SendAsync("""{"kind":"future-kind","data":"unknown"}""");
        await feed.SendAsync("""{"kind":"data","data":"second","channel":"order:7","topic":"status"}""");

        var messages = await received.WaitAsync(Wait, Cancel);
        messages.Select(message => message.Data.GetString()).ShouldBe(["first", "second"]);
    }

    [Fact]
    public async Task Typed_data_is_read_with_the_serializer_options_of_the_app()
    {
        var feed = new SseFeed();
        _relay.Answer(feed);
        var status = new RealtimeTopic<RelayProgress>("order:7", "status");
        var received = Collect(Source().SubscribeAsync(status, Cancel), count: 1);

        await feed.SendAsync("""{"kind":"data","data":{"text":"Packed","percent":50},"channel":"order:7","topic":"status"}""");

        (await received.WaitAsync(Wait, Cancel)).Single().Data.ShouldBe(new RelayProgress("Packed", 50));
    }

    [Fact]
    public async Task Typed_data_can_be_read_without_reflection_from_a_source_generated_context()
    {
        var feed = new SseFeed();
        _relay.Answer(feed);
        var source = Source(options => options.SerializerOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web) { TypeInfoResolver = RelayJsonContext.Default });
        var received = Collect(source.SubscribeAsync(new RealtimeTopic<RelayProgress>("order:7", "status"), Cancel), count: 1);

        await feed.SendAsync("""{"kind":"data","data":{"text":"Packed","percent":50},"channel":"order:7","topic":"status"}""");

        (await received.WaitAsync(Wait, Cancel)).Single().Data.ShouldBe(new RelayProgress("Packed", 50));

        // A type the context does not know is refused, not read by reflection.
        Should.Throw<NotSupportedException>(() => source.SubscribeAsync(new RealtimeTopic<NotInTheContext>("order:7", "status"), Cancel));
    }

    [Fact]
    public async Task A_message_whose_data_is_not_the_topics_type_is_skipped()
    {
        var feed = new SseFeed();
        _relay.Answer(feed);
        var received = Collect(Source().SubscribeAsync(new RealtimeTopic<RelayProgress>("order:7", "status"), Cancel), count: 1);

        await feed.SendAsync("""{"kind":"data","data":"not a progress","channel":"order:7","topic":"status"}""");
        await feed.SendAsync("this is not json at all");
        await feed.SendAsync("""{"kind":"data","data":{"text":"Packed","percent":50},"channel":"order:7","topic":"status"}""");

        (await received.WaitAsync(Wait, Cancel)).Single().Data.ShouldBe(new RelayProgress("Packed", 50));
    }

    [Fact]
    public async Task The_request_asks_for_an_event_stream_and_for_the_response_to_be_streamed()
    {
        var feed = new SseFeed();
        _relay.Answer(feed);
        var received = Collect(Source().SubscribeAsync("order:7", ["status", "tokens"], Cancel), count: 1);
        await feed.SendAsync("""{"kind":"data","data":1,"channel":"order:7","topic":"status"}""");
        await received.WaitAsync(Wait, Cancel);

        var request = _relay.Requests.Single();

        request.Url.ToString().ShouldBe("http://relay.test/realtime/order%3A7?topics=status,tokens");
        request.Accept.ShouldBe(["text/event-stream"]);
        request.StreamsTheResponse.ShouldBeTrue();
    }

    [Fact]
    public async Task A_connection_that_ends_is_opened_again_after_a_delay_and_the_states_are_reported()
    {
        var first = new SseFeed();
        var second = new SseFeed();
        _relay.Answer(first);
        _relay.Answer(second);
        var states = new List<RealtimeConnectionState>();
        var received = Collect(Source().SubscribeAsync("order:7", ["status"], Cancel, state => { lock (states) { states.Add(state); } }), count: 2);

        await first.SendAsync(Message("A"));
        await UntilAsync(() => States(states).Count >= 2);
        await first.EndAsync();
        await UntilAsync(() => States(states).Count >= 3);
        _relay.Requests.Count.ShouldBe(1);
        await AdvanceUntilAsync(() => _relay.Requests.Count == 2, TimeSpan.FromSeconds(1));
        await second.SendAsync(Message("B"));

        (await received.WaitAsync(Wait, Cancel)).Select(message => message.Data.GetString()).ShouldBe(["A", "B"]);
        await UntilAsync(() => States(states).Count >= 4);
        States(states).ShouldBe([RealtimeConnectionState.Connecting, RealtimeConnectionState.Open, RealtimeConnectionState.Reconnecting, RealtimeConnectionState.Open]);
    }

    [Fact]
    public async Task A_connection_that_lived_long_enough_is_opened_again_at_once()
    {
        var first = new SseFeed();
        var second = new SseFeed();
        _relay.Answer(first);
        _relay.Answer(second);
        var received = Collect(Source().SubscribeAsync("order:7", ["status"], Cancel), count: 2);

        await first.SendAsync(Message("A"));
        await UntilAsync(() => _relay.Requests.Count == 1);
        _time.Advance(TimeSpan.FromSeconds(10));
        await first.EndAsync();
        await UntilAsync(() => _relay.Requests.Count == 2);
        await second.SendAsync(Message("B"));

        (await received.WaitAsync(Wait, Cancel)).Select(message => message.Data.GetString()).ShouldBe(["A", "B"]);
    }

    [Fact]
    public async Task A_relay_that_keeps_closing_connections_is_met_with_longer_delays()
    {
        // Each connection ends at once, so each counts as a failure.
        for (var connection = 0; connection < 4; connection++)
        {
            var feed = new SseFeed();
            _relay.Answer(feed);
            await feed.EndAsync();
        }

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Cancel);
        var subscription = Collect(Source().SubscribeAsync("order:7", ["status"], stop.Token), count: 1);

        // The idle timeout of 45 seconds is a timer too, so only the delays are looked at.
        List<TimeSpan> Delays() => [.. _time.Timers.Where(due => due < TimeSpan.FromSeconds(40))];
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            await UntilAsync(() => Delays().Count == attempt);
            _time.Advance(TimeSpan.FromSeconds(5));
            await UntilAsync(() => _relay.Requests.Count == attempt + 1);
        }

        // About 0.5, 1 and 2 seconds, each up to a fifth less.
        var delays = Delays();
        delays[0].ShouldBeInRange(TimeSpan.FromMilliseconds(400), TimeSpan.FromMilliseconds(500));
        delays[1].ShouldBeInRange(TimeSpan.FromMilliseconds(800), TimeSpan.FromMilliseconds(1000));
        delays[2].ShouldBeInRange(TimeSpan.FromMilliseconds(1600), TimeSpan.FromMilliseconds(2000));
        await stop.CancelAsync();
        await Should.ThrowAsync<OperationCanceledException>(() => subscription.WaitAsync(Wait, Cancel));
    }

    [Fact]
    public async Task A_failure_of_the_relay_or_the_network_is_retried()
    {
        var feed = new SseFeed();
        _relay.Answer(HttpStatusCode.ServiceUnavailable, "text/plain", "Try later.");
        _relay.Answer(HttpStatusCode.BadGateway);
        _relay.Fail(new HttpRequestException("TypeError: Failed to fetch"));
        _relay.Answer(HttpStatusCode.TooManyRequests);
        _relay.Answer(feed);
        var states = new List<RealtimeConnectionState>();
        var received = Collect(Source().SubscribeAsync("order:7", ["status"], Cancel, state => { lock (states) { states.Add(state); } }), count: 1);

        await AdvanceUntilAsync(() => _relay.Requests.Count == 5, TimeSpan.FromSeconds(2));
        await feed.SendAsync(Message("A"));

        (await received.WaitAsync(Wait, Cancel)).Single().Data.GetString().ShouldBe("A");

        // It never connected before the last of them, so it did not claim to be reconnecting.
        States(states).ShouldContain(RealtimeConnectionState.Open);
        States(states).ShouldNotContain(RealtimeConnectionState.Reconnecting);
    }

    [Fact]
    public async Task A_refusal_ends_the_subscription_and_is_not_retried()
    {
        _relay.Answer(HttpStatusCode.Forbidden);

        var exception = await Should.ThrowAsync<HttpRequestException>(() => Collect(Source().SubscribeAsync("order:7", ["status"], Cancel), count: 1));

        exception.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        exception.Message.ShouldBe("The realtime relay answered 403 Forbidden.");
        _relay.Requests.Count.ShouldBe(1);
    }

    [Fact]
    public async Task An_unauthorized_request_and_a_missing_endpoint_are_refusals_too()
    {
        _relay.Answer(HttpStatusCode.Unauthorized);
        _relay.Answer(HttpStatusCode.NotFound);
        var source = Source();

        var unauthorized = await Should.ThrowAsync<HttpRequestException>(() => Collect(source.SubscribeAsync("order:7", ["status"], Cancel), count: 1));
        var missing = await Should.ThrowAsync<HttpRequestException>(() => Collect(source.SubscribeAsync("order:7", ["status"], Cancel), count: 1));

        unauthorized.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        missing.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task An_answer_that_is_not_an_event_stream_is_refused_as_an_EventSource_would()
    {
        // A wrong address falls back to the page of the app, which is a 200.
        _relay.Answer(HttpStatusCode.OK, "text/html", "<html></html>");

        var exception = await Should.ThrowAsync<HttpRequestException>(() => Collect(Source().SubscribeAsync("order:7", ["status"], Cancel), count: 1));

        exception.Message.ShouldBe("The realtime relay answered text/html instead of text/event-stream. Is the address right?");
        _relay.Requests.Count.ShouldBe(1);
    }

    [Fact]
    public async Task No_content_ends_the_subscription_as_an_EventSource_understands_it()
    {
        _relay.Answer(HttpStatusCode.NoContent);

        var messages = await Collect(Source().SubscribeAsync("order:7", ["status"], Cancel), count: 1);

        messages.ShouldBeEmpty();
        _relay.Requests.Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_connection_that_goes_quiet_is_taken_for_dead_and_opened_again()
    {
        var first = new SseFeed();
        var second = new SseFeed();
        _relay.Answer(first);
        _relay.Answer(second);
        var received = Collect(Source().SubscribeAsync("order:7", ["status"], Cancel), count: 1);

        await first.WriteAsync(":\n\n");
        await UntilAsync(() => _time.Timers.Count >= 1);
        _time.Advance(TimeSpan.FromSeconds(46));
        await AdvanceUntilAsync(() => _relay.Requests.Count == 2, TimeSpan.FromSeconds(1));
        await second.SendAsync(Message("A"));

        (await received.WaitAsync(Wait, Cancel)).Single().Data.GetString().ShouldBe("A");
    }

    [Fact]
    public async Task Cancelling_ends_the_subscription_and_opens_nothing_again()
    {
        var feed = new SseFeed();
        _relay.Answer(feed);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Cancel);
        var received = Collect(Source().SubscribeAsync("order:7", ["status"], stop.Token), count: 2);
        await feed.SendAsync(Message("A"));
        await UntilAsync(() => _relay.Requests.Count == 1);

        await stop.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(() => received.WaitAsync(Wait, Cancel));
        await Task.Delay(100, Cancel);
        _relay.Requests.Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_relative_address_is_relative_to_the_base_address_of_the_client_or_else_of_the_page()
    {
        Action<RealtimeRelayOptions> relative = options => options.Url = (channel, _) => new Uri($"/api/realtime/{Uri.EscapeDataString(channel)}", UriKind.Relative);
        Action<IServiceCollection> page = services => services.AddSingleton<NavigationManager>(new PageNavigationManager("https://app.test/"));
        var ofThePage = Source(relative, page);
        var ofTheClient = Source(relative, page, client => client.ConfigureHttpClient(http => http.BaseAddress = new Uri("https://api.test/")));

        foreach (var (source, channel) in new[] { (ofThePage, "order:7"), (ofTheClient, "order:8") })
        {
            var feed = new SseFeed();
            _relay.Answer(feed);
            var received = Collect(source.SubscribeAsync(channel, ["status"], Cancel), count: 1);
            await feed.SendAsync($$"""{"kind":"data","data":1,"channel":"{{channel}}","topic":"status"}""");
            await received.WaitAsync(Wait, Cancel);
        }

        _relay.Requests.Select(request => request.Url.ToString()).ShouldBe(["https://app.test/api/realtime/order%3A7", "https://api.test/api/realtime/order%3A8"]);
    }

    [Fact]
    public async Task Without_an_address_subscribing_says_what_to_set()
    {
        var collection = new ServiceCollection().AddLogging();
        collection.AddInngestRealtimeRelay(_ => { }).ConfigurePrimaryHttpMessageHandler(() => _relay);
        var source = Build(collection);

        var exception = await Should.ThrowAsync<InvalidOperationException>(() => Collect(source.SubscribeAsync("order:7", ["status"], Cancel), count: 1));

        exception.Message.ShouldContain("RealtimeRelayOptions.Url");
    }

    [Fact]
    public void Arguments_are_checked_when_subscribing_not_when_reading()
    {
        var source = Source();

        Should.Throw<ArgumentException>(() => source.SubscribeAsync("", ["status"], Cancel));
        Should.Throw<ArgumentException>(() => source.SubscribeAsync("order:7", [""], Cancel));
        Should.Throw<ArgumentException>(() => source.SubscribeAsync(new RealtimeTopic<RelayProgress>("order:7", ""), Cancel));
        _relay.Requests.ShouldBeEmpty();
    }

    private IRealtimeSource Source(
        Action<RealtimeRelayOptions>? configure = null,
        Action<IServiceCollection>? services = null,
        Action<IHttpClientBuilder>? client = null)
    {
        var collection = new ServiceCollection().AddLogging();
        collection.AddSingleton<TimeProvider>(_time);
        services?.Invoke(collection);
        var http = collection.AddInngestRealtimeRelay(options =>
        {
            options.Url = (channel, topics) => new Uri($"http://relay.test/realtime/{Uri.EscapeDataString(channel)}?topics={string.Join(',', topics)}");
            configure?.Invoke(options);
        });
        http.ConfigurePrimaryHttpMessageHandler(() => _relay);
        client?.Invoke(http);
        return Build(collection);
    }

    private IRealtimeSource Build(IServiceCollection collection)
    {
        var provider = collection.BuildServiceProvider();
        _providers.Add(provider);
        return provider.CreateScope().ServiceProvider.GetRequiredService<IRealtimeSource>();
    }

    private static string Message(string text) => $$"""{"kind":"data","data":"{{text}}","channel":"order:7","topic":"status"}""";

    private static List<RealtimeConnectionState> States(List<RealtimeConnectionState> states)
    {
        lock (states)
        {
            return [.. states];
        }
    }

    private static async Task<List<RealtimeMessage<T>>> Collect<T>(IAsyncEnumerable<RealtimeMessage<T>> source, int count)
    {
        var all = new List<RealtimeMessage<T>>();
        await foreach (var message in source)
        {
            all.Add(message);
            if (all.Count == count)
            {
                break;
            }
        }

        return all;
    }

    // Moves the clock on in steps until the condition holds, so that the delays between connections elapse.
    private async Task AdvanceUntilAsync(Func<bool> condition, TimeSpan step)
    {
        var deadline = DateTime.UtcNow + Wait;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("The condition was not met in time.");
            }

            _time.Advance(step);
            await Task.Delay(20, Cancel);
        }
    }

    private static async Task UntilAsync(Func<bool> condition)
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

    private sealed class PageNavigationManager : NavigationManager
    {
        public PageNavigationManager(string baseUri) => Initialize(baseUri, baseUri);

        protected override void NavigateToCore(string uri, NavigationOptions options)
        {
        }
    }
}

internal sealed record RelayProgress(string Text, int Percent);

internal sealed record NotInTheContext(string Text);

[JsonSerializable(typeof(RelayProgress))]
internal sealed partial class RelayJsonContext : JsonSerializerContext;

// Fake time that remembers how long each timer was set for.
internal sealed class RecordingTimeProvider(DateTimeOffset start) : FakeTimeProvider(start)
{
    private readonly List<TimeSpan> _timers = [];

    public IReadOnlyList<TimeSpan> Timers
    {
        get
        {
            lock (_timers)
            {
                return [.. _timers];
            }
        }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        lock (_timers)
        {
            _timers.Add(dueTime);
        }

        return base.CreateTimer(callback, state, dueTime, period);
    }
}
