using Inngest.Realtime;
using Inngest.Tests.Realtime;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Inngest.Tests.SignalR;

internal sealed class TestHub : Hub;

internal sealed record OrderStatus(string Text, int Percent);

// Inngest's realtime API with the channel and topics of every token that was minted written down, for the tests of what
// a subscription asks Inngest for.
internal sealed class RecordingRealtimeApi(FakeRealtimeApi inner) : IRealtimeApi
{
    public List<(string Channel, string[] Topics)> Tokens { get; } = [];

    public Task PublishAsync(RealtimeTopic topic, string? runId, ReadOnlyMemory<byte> body, CancellationToken cancellationToken) =>
        inner.PublishAsync(topic, runId, body, cancellationToken);

    public Task<string> PublishStreamAsync(RealtimeTopic topic, string? runId, IAsyncEnumerable<string> chunks, CancellationToken cancellationToken) =>
        inner.PublishStreamAsync(topic, runId, chunks, cancellationToken);

    public Task<string> GetTokenAsync(string channel, IReadOnlyList<string> topics, CancellationToken cancellationToken)
    {
        lock (Tokens)
        {
            Tokens.Add((channel, [.. topics]));
        }

        return inner.GetTokenAsync(channel, topics, cancellationToken);
    }

    public Task<HttpResponseMessage> OpenSubscriptionAsync(string token, CancellationToken cancellationToken) =>
        inner.OpenSubscriptionAsync(token, cancellationToken);
}

// One instance of an app: its own subscriptions to Inngest and the bridge of its own, over the hub every instance of the
// rig shares.
internal sealed class Instance(FakeRealtimeApi api, RecordingRealtimeApi recorded, InngestRealtimeBridge<TestHub> bridge, CancellationTokenSource stopping)
{
    public FakeRealtimeApi Api { get; } = api;

    // The tokens of the subscriptions opened so far, which a test reads while subscriptions are still being opened.
    public IReadOnlyList<CancellationToken> Subscriptions
    {
        get
        {
            lock (Api.Subscriptions)
            {
                return [.. Api.Subscriptions];
            }
        }
    }

    public RecordingRealtimeApi Recorded { get; } = recorded;

    public InngestRealtimeBridge<TestHub> Bridge { get; } = bridge;

    // Cancelling it is the app stopping.
    public CancellationTokenSource Stopping { get; } = stopping;
}

// Bridges over a hub whose lifetime manager is a fake that every instance shares, the way instances share a backplane.
internal sealed class BridgeRig : IDisposable
{
    private readonly ServiceProvider _provider;

    public BridgeRig()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSignalR();
        services.AddSingleton<HubLifetimeManager<TestHub>>(Hubs);
        services.AddScoped<ScopedProbe>();
        _provider = services.BuildServiceProvider();
    }

    public RecordingLifetimeManager<TestHub> Hubs { get; } = new();

    public static Func<RealtimeSubscribeRequest, ValueTask<RealtimeSubscription?>> AllowAll { get; } =
        request => ValueTask.FromResult<RealtimeSubscription?>(request.Requested);

    public Instance AddInstance(Func<RealtimeSubscribeRequest, ValueTask<RealtimeSubscription?>>? authorize = null, Action<InngestSignalROptions>? configure = null)
    {
        var api = new FakeRealtimeApi();
        var recorded = new RecordingRealtimeApi(api);
        var options = new InngestSignalROptions { Authorize = authorize ?? AllowAll };
        configure?.Invoke(options);
        var stopping = new CancellationTokenSource();
        var bridge = new InngestRealtimeBridge<TestHub>(
            new RealtimeSubscriber(recorded, TimeProvider.System, NullLogger<RealtimeSubscriber>.Instance),
            _provider.GetRequiredService<IHubContext<TestHub>>(),
            _provider.GetRequiredService<IServiceScopeFactory>(),
            options,
            NullLogger.Instance,
            stopping.Token);
        return new Instance(api, recorded, bridge, stopping);
    }

    public void Dispose() => _provider.Dispose();
}

// A scoped service, to see that the callback of Authorize gets a scope of its own.
internal sealed class ScopedProbe : IDisposable
{
    public bool IsDisposed { get; private set; }

    public void Dispose() => IsDisposed = true;
}

internal static class Wait
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    // A condition that throws is not met yet, such as one that reads a subscription the bridge has still to open on a
    // task of its own; the last failure goes into the timeout.
    public static async Task UntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Timeout;
        Exception? failure = null;
        while (true)
        {
            try
            {
                if (condition())
                {
                    return;
                }
            }
            catch (Exception exception) when (exception is ArgumentOutOfRangeException or InvalidOperationException)
            {
                failure = exception;
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("The condition was not met in time.", failure);
            }

            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
    }
}
