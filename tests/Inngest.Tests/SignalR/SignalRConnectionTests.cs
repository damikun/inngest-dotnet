using System.Threading.Channels;
using Inngest.Realtime;
using Inngest.Tests.Realtime;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Inngest.Tests.SignalR;

// The bridge on an in-memory server with real SignalR clients, which connect over WebSockets, and Inngest's realtime API
// scripted.
public sealed class SignalRConnectionTests
{
    private const string Order = "order:o-1";
    private const string Message = """{"kind":"data","data":{"text":"Packed","percent":50},"channel":"order:o-1","topic":"status","run_id":"01J"}""";

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    private readonly FakeRealtimeApi _api = new();

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_client_joins_through_the_hub_and_receives_the_message_as_Inngest_sent_it()
    {
        var feed = new SseFeed();
        _api.Answer(feed);
        await using var app = await StartAsync();
        await using var client = new Client(app);
        await client.StartAsync();

        await client.Connection.InvokeAsync("Subscribe", Order, new[] { "status" }, Cancel);
        await feed.SendAsync(Message);

        (await client.NextMessageAsync()).ShouldBe(Message);
        _api.TokensMinted.ShouldBe(1);
    }

    [Fact]
    public async Task The_message_reaches_a_client_that_speaks_MessagePack_alike()
    {
        var feed = new SseFeed();
        _api.Answer(feed);

        // Only MessagePack is allowed, so that a client that connects has spoken it.
        await using var app = await StartAsync(signalR => signalR.AddMessagePackProtocol().AddHubOptions<InngestRealtimeHub>(options => options.SupportedProtocols = ["messagepack"]));
        await using var client = new Client(app, messagePack: true);
        await client.StartAsync();
        await using var json = new Client(app);
        await Should.ThrowAsync<Exception>(json.StartAsync);

        await client.Connection.InvokeAsync("Subscribe", Order, new[] { "status" }, Cancel);
        await feed.SendAsync(Message);

        (await client.NextMessageAsync()).ShouldBe(Message);
    }

    [Fact]
    public async Task Only_the_clients_that_joined_receive_it_and_each_receives_it_once()
    {
        var feed = new SseFeed();
        _api.Answer(feed);
        await using var app = await StartAsync();
        await using var first = new Client(app);
        await using var second = new Client(app);
        await using var bystander = new Client(app);
        await first.StartAsync();
        await second.StartAsync();
        await bystander.StartAsync();
        await first.Connection.InvokeAsync("Subscribe", Order, new[] { "status" }, Cancel);
        await second.Connection.InvokeAsync("Subscribe", Order, new[] { "status", "tokens" }, Cancel);
        await second.Connection.InvokeAsync("Unsubscribe", Order, new[] { "status", "tokens" }, Cancel);
        await second.Connection.InvokeAsync("Subscribe", Order, new[] { "status" }, Cancel);

        await feed.SendAsync(Message);
        await feed.SendAsync("""{"kind":"data","n":2}""");

        (await first.NextMessageAsync()).ShouldBe(Message);
        (await first.NextMessageAsync()).ShouldBe("""{"kind":"data","n":2}""");
        (await second.NextMessageAsync()).ShouldBe(Message);
        (await second.NextMessageAsync()).ShouldBe("""{"kind":"data","n":2}""");
        await Task.Delay(300, Cancel);
        first.Received.ShouldBe(2);
        second.Received.ShouldBe(2);
        bystander.Received.ShouldBe(0);
    }

    [Fact]
    public async Task A_client_the_callback_refuses_gets_a_HubException_with_the_reason()
    {
        await using var app = await StartAsync(authorize: request => request.Requested.Channel == Order
            ? throw new HubException("That order is not yours.")
            : ValueTask.FromResult<RealtimeSubscription?>(null));
        await using var client = new Client(app);
        await client.StartAsync();

        var thrown = await Should.ThrowAsync<HubException>(() => client.Connection.InvokeAsync("Subscribe", Order, new[] { "status" }, Cancel));
        var refused = await Should.ThrowAsync<HubException>(() => client.Connection.InvokeAsync("Subscribe", "order:o-2", new[] { "status" }, Cancel));

        thrown.Message.ShouldContain("That order is not yours.");
        refused.Message.ShouldContain("You may not subscribe to this channel.");
        _api.TokensMinted.ShouldBe(0);
    }

    [Fact]
    public async Task The_callback_sees_the_connection_that_asks()
    {
        string? seen = null;
        await using var app = await StartAsync(authorize: request =>
        {
            seen = request.Caller.ConnectionId;
            return ValueTask.FromResult<RealtimeSubscription?>(request.Requested);
        });
        _api.Answer(new SseFeed());
        await using var client = new Client(app);
        await client.StartAsync();

        await client.Connection.InvokeAsync("Subscribe", Order, new[] { "status" }, Cancel);

        seen.ShouldBe(client.Connection.ConnectionId);
    }

    [Fact]
    public async Task The_subscription_closes_when_the_last_client_stops_its_connection()
    {
        _api.Answer(new SseFeed());
        await using var app = await StartAsync();
        var bridge = app.Services.GetRequiredService<InngestRealtimeBridge<InngestRealtimeHub>>();
        await using var client = new Client(app);
        await client.StartAsync();
        await client.Connection.InvokeAsync("Subscribe", Order, new[] { "status" }, Cancel);
        await Wait.UntilAsync(() => _api.Subscriptions.Count == 1);
        _api.Subscriptions[0].IsCancellationRequested.ShouldBeFalse();

        await client.Connection.StopAsync(Cancel);

        await Wait.UntilAsync(() => _api.Subscriptions[0].IsCancellationRequested);
        await Wait.UntilAsync(() => bridge.FeedCount == 0 && bridge.ListenerCount == 0);
    }

    [Fact]
    public async Task A_reconnected_client_has_a_new_connection_and_has_to_join_again()
    {
        var first = new SseFeed();
        var second = new SseFeed();
        _api.Answer(first);
        _api.Answer(second);
        await using var app = await StartAsync();
        var bridge = app.Services.GetRequiredService<InngestRealtimeBridge<InngestRealtimeHub>>();
        await using var client = new Client(app);
        await client.StartAsync();
        var firstId = client.Connection.ConnectionId;
        await client.Connection.InvokeAsync("Subscribe", Order, new[] { "status" }, Cancel);
        await client.Connection.StopAsync(Cancel);
        await Wait.UntilAsync(() => _api.Subscriptions[0].IsCancellationRequested);

        await client.Connection.StartAsync(Cancel);

        client.Connection.ConnectionId.ShouldNotBe(firstId);
        bridge.ListenerCount.ShouldBe(0);
        await client.Connection.InvokeAsync("Subscribe", Order, new[] { "status" }, Cancel);
        await second.SendAsync("""{"kind":"data","n":2}""");
        (await client.NextMessageAsync()).ShouldBe("""{"kind":"data","n":2}""");
        bridge.ListenerCount.ShouldBe(1);
    }

    [Fact]
    public async Task A_subscription_Inngest_refuses_reaches_the_client_as_an_error()
    {
        _api.TokenFailure = new InngestException("Failed to get a subscription token for channel \"order:o-1\": 401 Unauthorized", 401);
        await using var app = await StartAsync();
        await using var client = new Client(app);
        await client.StartAsync();

        await client.Connection.InvokeAsync("Subscribe", Order, new[] { "status" }, Cancel);

        (await client.NextErrorAsync()).ShouldBe((Order, "The subscription to Inngest ended."));
    }

    [Fact]
    public async Task A_hub_of_the_app_joins_its_clients_to_typed_topics_through_the_bridge()
    {
        var feed = new SseFeed();
        _api.Answer(feed);
        await using var app = await StartAsync(custom: true);
        await using var client = new Client(app, "/watch");
        await client.StartAsync();

        await client.Connection.InvokeAsync("Watch", "o-1", Cancel);
        await feed.SendAsync(Message);

        (await client.NextMessageAsync()).ShouldBe(Message);
        _api.TokensMinted.ShouldBe(1);
        await client.Connection.InvokeAsync("Unwatch", "o-1", Cancel);
        await Wait.UntilAsync(() => _api.Subscriptions[0].IsCancellationRequested);
    }

    [Fact]
    public async Task The_subscriptions_close_when_the_app_stops()
    {
        _api.Answer(new SseFeed());
        var app = await StartAsync();
        await using var client = new Client(app);
        await client.StartAsync();
        await client.Connection.InvokeAsync("Subscribe", Order, new[] { "status" }, Cancel);
        await Wait.UntilAsync(() => _api.Subscriptions.Count == 1);

        await app.StopAsync(Cancel);

        await Wait.UntilAsync(() => _api.Subscriptions[0].IsCancellationRequested);
        await app.DisposeAsync();
    }

    [Fact]
    public void The_bridge_needs_an_authorize_callback_and_sensible_options()
    {
        var services = new ServiceCollection();

        var missing = Should.Throw<InvalidOperationException>(() => services.AddInngestSignalR(_ => { }));
        var same = Should.Throw<InvalidOperationException>(() => services.AddInngestSignalR(options =>
        {
            options.Authorize = request => ValueTask.FromResult<RealtimeSubscription?>(request.Requested);
            options.ErrorMethod = "realtime";
        }));
        var none = Should.Throw<InvalidOperationException>(() => services.AddInngestSignalR(options =>
        {
            options.Authorize = request => ValueTask.FromResult<RealtimeSubscription?>(request.Requested);
            options.MaxSubscriptionsPerConnection = 0;
        }));

        missing.Message.ShouldContain("Authorize is required");
        same.Message.ShouldContain("must differ");
        none.Message.ShouldContain("at least 1");
    }

    [Fact]
    public async Task Mapping_the_hub_without_registering_the_bridge_fails_at_startup()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSignalR();
        builder.Services.AddInngest(options =>
        {
            options.AppId = "shop";
            options.IsDev = true;
        });
        await using var app = builder.Build();

        var exception = Should.Throw<InvalidOperationException>(() => app.MapInngestRealtimeHub("/hub"));

        exception.Message.ShouldContain("AddInngestSignalR");
    }

    private async Task<WebApplication> StartAsync(
        Action<ISignalRServerBuilder>? signalR = null,
        Func<RealtimeSubscribeRequest, ValueTask<RealtimeSubscription?>>? authorize = null,
        bool custom = false)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        var server = builder.Services.AddSignalR();
        signalR?.Invoke(server);
        builder.Services.AddInngest(options =>
        {
            options.AppId = "shop";
            options.IsDev = true;
        });
        builder.Services.Replace(ServiceDescriptor.Singleton<IRealtimeApi>(_api));
        authorize ??= request => ValueTask.FromResult<RealtimeSubscription?>(request.Requested);
        if (custom)
        {
            builder.Services.AddInngestSignalR<WatchHub>(options => options.Authorize = authorize);
        }
        else
        {
            builder.Services.AddInngestSignalR(options => options.Authorize = authorize);
        }

        var app = builder.Build();
        if (custom)
        {
            app.MapHub<WatchHub>("/watch");
        }
        else
        {
            app.MapInngestRealtimeHub("/hub");
        }

        await app.StartAsync(Cancel);
        return app;
    }

    // A SignalR client of the in-memory server that keeps what it is sent.
    private sealed class Client : IAsyncDisposable
    {
        private readonly Channel<string> _messages = Channel.CreateUnbounded<string>();
        private readonly Channel<(string Channel, string Text)> _errors = Channel.CreateUnbounded<(string, string)>();
        private int _received;

        public Client(WebApplication app, string path = "/hub", bool messagePack = false)
        {
            var server = app.GetTestServer();
            var builder = new HubConnectionBuilder().WithUrl(new Uri(server.BaseAddress, path), options =>
            {
                options.Transports = HttpTransportType.WebSockets;
                options.HttpMessageHandlerFactory = _ => server.CreateHandler();
                options.WebSocketFactory = async (context, cancellationToken) => await server.CreateWebSocketClient().ConnectAsync(context.Uri, cancellationToken);
            });
            if (messagePack)
            {
                builder.AddMessagePackProtocol();
            }

            Connection = builder.Build();
            Connection.On<string>("Realtime", message =>
            {
                Interlocked.Increment(ref _received);
                _messages.Writer.TryWrite(message);
            });
            Connection.On<string, string>("RealtimeError", (channel, text) => _errors.Writer.TryWrite((channel, text)));
        }

        public HubConnection Connection { get; }

        public int Received => Volatile.Read(ref _received);

        public Task StartAsync() => Connection.StartAsync(Cancel);

        public async Task<string> NextMessageAsync() => await _messages.Reader.ReadAsync(Cancel).AsTask().WaitAsync(Timeout, Cancel);

        public async Task<(string Channel, string Text)> NextErrorAsync() => await _errors.Reader.ReadAsync(Cancel).AsTask().WaitAsync(Timeout, Cancel);

        public ValueTask DisposeAsync() => Connection.DisposeAsync();
    }

    // A hub of the app that calls the bridge itself, with a topic the functions publish to.
    private sealed class WatchHub(InngestRealtimeBridge<WatchHub> bridge) : Hub
    {
        public async Task Watch(string orderId) =>
            await bridge.SubscribeAsync(Context, new RealtimeTopic<OrderStatus>($"order:{orderId}", "status"), Context.ConnectionAborted);

        public void Unwatch(string orderId) => bridge.Unsubscribe(Context, new RealtimeTopic<OrderStatus>($"order:{orderId}", "status"));
    }
}
