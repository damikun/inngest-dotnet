using System.Runtime.CompilerServices;
using System.Text.Json;
using Bunit;
using Inngest.Realtime;
using Inngest.Tests.Execution;
using Inngest.Tests.Realtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Inngest.Tests.Blazor;

// The source of the server, over the SDK's own realtime with Inngest's API scripted: it says what the connection to
// Inngest is doing, which the public members of IInngestRealtime cannot.
public sealed class ServerRealtimeSourceTests : BunitContext
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private readonly FakeRealtimeApi _api = new();
    private readonly TestTimeProvider _time = new(EngineHost.Now);

    private static CancellationToken Cancel => Xunit.TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_view_on_a_quiet_topic_reads_Open_as_soon_as_the_connection_is_open()
    {
        _api.Answer(new SseFeed());
        AddServer();

        var cut = Render<RealtimeView<ServerProgress>>(parameters => parameters
            .Add(view => view.Topic, new RealtimeTopic<ServerProgress>("order:7", "status"))
            .Add(view => view.ChildContent, state => builder => builder.AddContent(0, $"{state.Connection} with {state.Messages.Count} messages")));

        cut.WaitForAssertion(() => cut.Markup.ShouldBe("Open with 0 messages"), Wait);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task The_source_says_when_the_connection_opens_is_lost_and_opens_again()
    {
        var first = new SseFeed();
        var second = new SseFeed();
        _api.Answer(first);
        _api.Answer(second);
        AddServer();
        var source = Services.GetRequiredService<IRealtimeSource>();
        var states = new List<RealtimeConnectionState>();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Cancel);
        var reading = Task.Run(
            async () =>
            {
                await foreach (var _ in source.SubscribeAsync(new RealtimeTopic<ServerProgress>("order:7", "status"), state => { lock (states) { states.Add(state); } }, stop.Token))
                {
                }
            },
            Cancel);

        await UntilAsync(() => States(states).Count == 2);
        _time.Advance(TimeSpan.FromSeconds(10));
        await first.EndAsync();
        await UntilAsync(() => States(states).Count == 4);

        States(states).ShouldBe([RealtimeConnectionState.Connecting, RealtimeConnectionState.Open, RealtimeConnectionState.Reconnecting, RealtimeConnectionState.Open]);
        await stop.CancelAsync();
        await Should.ThrowAsync<OperationCanceledException>(() => reading.WaitAsync(Wait, Cancel));
    }

    [Fact]
    public async Task A_channel_with_topics_says_the_same()
    {
        _api.Answer(new SseFeed());
        AddServer();
        var states = new List<RealtimeConnectionState>();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Cancel);

        var reading = Task.Run(
            async () =>
            {
                await foreach (var _ in Services.GetRequiredService<IRealtimeSource>().SubscribeAsync("order:7", ["status"], state => { lock (states) { states.Add(state); } }, stop.Token))
                {
                }
            },
            Cancel);
        await UntilAsync(() => States(states).Count == 2);

        States(states).ShouldBe([RealtimeConnectionState.Connecting, RealtimeConnectionState.Open]);
        await stop.CancelAsync();
        await Should.ThrowAsync<OperationCanceledException>(() => reading.WaitAsync(Wait, Cancel));
    }

    [Fact]
    public async Task Any_other_IInngestRealtime_is_read_through_its_public_members_and_says_nothing()
    {
        var other = new OtherRealtime();
        Services.AddSingleton<IInngestRealtime>(other);
        Services.AddInngestRealtimeServerSource();
        var states = new List<RealtimeConnectionState>();

        var messages = new List<string>();
        await foreach (var message in Services.GetRequiredService<IRealtimeSource>().SubscribeAsync(new RealtimeTopic<ServerProgress>("order:7", "status"), state => states.Add(state), Cancel))
        {
            messages.Add(message.Data!.Text);
        }

        messages.ShouldBe(["from the other implementation"]);
        states.ShouldBeEmpty();
        other.Subscribed.ShouldBe("order:7/status");
    }

    private void AddServer()
    {
        Services.AddInngest(options =>
        {
            options.AppId = "shop";
            options.IsDev = true;
        });
        Services.Replace(ServiceDescriptor.Singleton<IRealtimeApi>(_api));
        Services.Replace(ServiceDescriptor.Singleton<TimeProvider>(_time));
        Services.AddInngestRealtimeServerSource();
    }

    private static List<RealtimeConnectionState> States(List<RealtimeConnectionState> states)
    {
        lock (states)
        {
            return [.. states];
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

    private sealed record ServerProgress(string Text, int Percent);

    // An implementation of IInngestRealtime that is not the SDK's, such as a decorator would be.
    private sealed class OtherRealtime : IInngestRealtime
    {
        public string? Subscribed { get; private set; }

        public Task PublishAsync<T>(RealtimeTopic topic, T data, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task PublishAsync<T>(RealtimeTopic<T> topic, T data, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<string> PublishStreamAsync(RealtimeTopic topic, IAsyncEnumerable<string> chunks, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<RealtimeToken> GetSubscriptionTokenAsync(string channel, IReadOnlyList<string> topics, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public IAsyncEnumerable<RealtimeMessage<JsonElement>> SubscribeAsync(string channel, IReadOnlyList<string> topics, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public async IAsyncEnumerable<RealtimeMessage<T>> SubscribeAsync<T>(RealtimeTopic<T> topic, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Subscribed = $"{topic.Channel}/{topic.Name}";
            await Task.Yield();
            yield return new RealtimeMessage<T>(RealtimeMessageKind.Data, topic.Channel, topic.Name) { Data = (T)(object)new ServerProgress("from the other implementation", 1) };
        }
    }
}
