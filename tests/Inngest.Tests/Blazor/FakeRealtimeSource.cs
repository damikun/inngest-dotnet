using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;

namespace Inngest.Tests.Blazor;

// A realtime source whose subscriptions the test controls: it sends the messages, ends them and reports connections.
internal sealed class FakeRealtimeSource : IRealtimeSource
{
    private readonly Lock _gate = new();
    private readonly List<FakeSubscription> _subscriptions = [];

    // Makes subscriptions keep reading after they are cancelled, as a source that ignores its token would.
    public bool IgnoreCancellation { get; set; }

    // Thrown by the next call to SubscribeAsync, as a source does for arguments it refuses.
    public Exception? SubscribeFailure { get; set; }

    public IReadOnlyList<FakeSubscription> Subscriptions
    {
        get
        {
            lock (_gate)
            {
                return [.. _subscriptions];
            }
        }
    }

    public FakeSubscription<T> Subscription<T>(int index) => (FakeSubscription<T>)Subscriptions[index];

    public IAsyncEnumerable<RealtimeMessage<JsonElement>> SubscribeAsync(
        string channel,
        IReadOnlyList<string> topics,
        CancellationToken cancellationToken = default,
        Action<RealtimeConnectionState>? onConnectionChanged = null) =>
        Add(new FakeSubscription<JsonElement>(channel, [.. topics], cancellationToken, onConnectionChanged, IgnoreCancellation)).Read(cancellationToken);

    public IAsyncEnumerable<RealtimeMessage<T>> SubscribeAsync<T>(
        RealtimeTopic<T> topic,
        CancellationToken cancellationToken = default,
        Action<RealtimeConnectionState>? onConnectionChanged = null) =>
        Add(new FakeSubscription<T>(topic.Channel, [topic.Name], cancellationToken, onConnectionChanged, IgnoreCancellation)).Read(cancellationToken);

    private FakeSubscription<T> Add<T>(FakeSubscription<T> subscription)
    {
        if (SubscribeFailure is { } failure)
        {
            SubscribeFailure = null;
            throw failure;
        }

        lock (_gate)
        {
            _subscriptions.Add(subscription);
        }

        return subscription;
    }
}

internal abstract class FakeSubscription(string channel, IReadOnlyList<string> topics, CancellationToken token, Action<RealtimeConnectionState>? onConnectionChanged)
{
    private readonly TaskCompletionSource _ended = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public string Channel { get; } = channel;

    public IReadOnlyList<string> Topics { get; } = topics;

    // The token the subscriber cancels to unsubscribe.
    public CancellationToken Token { get; } = token;

    // Completes when the subscriber has stopped reading, which a subscriber that ends the enumeration does.
    public Task Ended => _ended.Task;

    public void Report(RealtimeConnectionState state) => onConnectionChanged?.Invoke(state);

    protected void End() => _ended.TrySetResult();
}

internal sealed class FakeSubscription<T>(
    string channel,
    IReadOnlyList<string> topics,
    CancellationToken token,
    Action<RealtimeConnectionState>? onConnectionChanged,
    bool ignoreCancellation) : FakeSubscription(channel, topics, token, onConnectionChanged)
{
    private readonly Channel<RealtimeMessage<T>> _messages = System.Threading.Channels.Channel.CreateUnbounded<RealtimeMessage<T>>();

    public void Send(RealtimeMessage<T> message) => _messages.Writer.TryWrite(message);

    public void Send(string topic, T data) => Send(new RealtimeMessage<T>(RealtimeMessageKind.Data, Channel, topic) { Data = data });

    // The enumeration throws this once the messages sent before it have been read.
    public void Fail(Exception failure) => _messages.Writer.TryComplete(failure);

    public void Complete() => _messages.Writer.TryComplete();

    public async IAsyncEnumerable<RealtimeMessage<T>> Read([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        try
        {
            await foreach (var message in _messages.Reader.ReadAllAsync(ignoreCancellation ? CancellationToken.None : cancellationToken))
            {
                yield return message;
            }
        }
        finally
        {
            End();
        }
    }
}
