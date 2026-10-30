using Bunit;
using Inngest.Realtime;
using Inngest.Tests.Realtime;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Inngest.Tests.Blazor;

// The relay source against the real MapInngestRealtime endpoint on an in-memory server, with Inngest's realtime API
// scripted: what the endpoint writes is what the source and a view read.
public sealed class RealtimeRelayEndpointTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private readonly FakeRealtimeApi _api = new();

    private static CancellationToken Cancel => Xunit.TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_source_reads_the_messages_the_endpoint_relays_and_a_view_shows_them()
    {
        var feed = new SseFeed();
        _api.Answer(feed);
        await using var relay = await StartRelayAsync();
        await using var context = new BunitContext();
        context.Services.AddLogging();
        context.Services
            .AddInngestRealtimeRelay(options => options.Url = (channel, _) => new Uri($"http://relay.test/realtime/{Uri.EscapeDataString(channel)}"))
            .ConfigurePrimaryHttpMessageHandler(() => relay.GetTestServer().CreateHandler());
        var topic = new RealtimeTopic<RelayProgress>("order:o-1", "status");

        var cut = context.Render<RealtimeView<RelayProgress>>(parameters => parameters
            .Add(view => view.Topic, topic)
            .Add(view => view.ChildContent, state => builder => builder.AddContent(0, $"{state.Connection}: {state.Latest?.Data?.Text} {state.Latest?.Data?.Percent}")));
        await feed.SendAsync("""{"kind":"data","data":{"text":"Packed","percent":50},"channel":"order:o-1","topic":"status","run_id":"01JRUN"}""");

        cut.WaitForAssertion(() => cut.Markup.ShouldBe("Open: Packed 50"), Wait);
        _api.TokensMinted.ShouldBe(1);
    }

    [Fact]
    public async Task A_channel_the_endpoint_refuses_fails_the_view_with_403()
    {
        await using var relay = await StartRelayAsync();
        await using var context = new BunitContext();
        context.Services.AddLogging();
        context.Services
            .AddInngestRealtimeRelay(options => options.Url = (channel, _) => new Uri($"http://relay.test/realtime/{Uri.EscapeDataString(channel)}"))
            .ConfigurePrimaryHttpMessageHandler(() => relay.GetTestServer().CreateHandler());

        var cut = context.Render<RealtimeView<RelayProgress>>(parameters => parameters
            .Add(view => view.Topic, new RealtimeTopic<RelayProgress>("order:forbidden", "status"))
            .Add(view => view.ChildContent, state => builder =>
            {
                builder.AddContent(0, state.Connection.ToString());
                builder.AddContent(1, (state.Error as HttpRequestException)?.StatusCode);
            }));

        cut.WaitForAssertion(() => cut.Markup.ShouldBe("FailedForbidden"), Wait);
        _api.TokensMinted.ShouldBe(0);
    }

    [Fact]
    public async Task A_client_the_endpoint_ended_reconnects_and_reads_on()
    {
        // Inngest refuses the endpoint's subscription, which ends the response of every client it serves.
        _api.TokenFailure = new InngestException("Failed to get a subscription token for channel \"order:o-1\": 401 Unauthorized", 401);
        var feed = new SseFeed();
        await using var relay = await StartRelayAsync();
        var services = new ServiceCollection().AddLogging();
        services
            .AddInngestRealtimeRelay(options => options.Url = (channel, _) => new Uri($"http://relay.test/realtime/{Uri.EscapeDataString(channel)}"))
            .ConfigurePrimaryHttpMessageHandler(() => relay.GetTestServer().CreateHandler());
        await using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var source = scope.ServiceProvider.GetRequiredService<IRealtimeSource>();
        var states = new List<RealtimeConnectionState>();

        var first = FirstAsync(source.SubscribeAsync(
            new RealtimeTopic<RelayProgress>("order:o-1", "status"),
            Cancel,
            state =>
            {
                lock (states)
                {
                    states.Add(state);
                }
            }));
        await UntilAsync(() => { lock (states) { return states.Contains(RealtimeConnectionState.Reconnecting); } });

        // The client waits a moment before it connects again, and Inngest accepts the subscription by then.
        _api.TokenFailure = null;
        _api.Answer(feed);
        await feed.SendAsync("""{"kind":"data","data":{"text":"B","percent":2},"channel":"order:o-1","topic":"status"}""");

        (await first.WaitAsync(Wait, Cancel)).Data.ShouldBe(new RelayProgress("B", 2));
        lock (states)
        {
            states.ShouldBe([RealtimeConnectionState.Connecting, RealtimeConnectionState.Open, RealtimeConnectionState.Reconnecting, RealtimeConnectionState.Open]);
        }
    }

    private async Task<WebApplication> StartRelayAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddInngest(options =>
        {
            options.AppId = "shop";
            options.IsDev = true;
        });
        builder.Services.Replace(ServiceDescriptor.Singleton<IRealtimeApi>(_api));
        var app = builder.Build();
        app.MapInngestRealtime("/realtime/{channel}", context =>
        {
            var channel = (string)context.Request.RouteValues["channel"]!;
            return ValueTask.FromResult(channel == "order:forbidden" ? null : new RealtimeSubscription(channel, ["status"]));
        });
        await app.StartAsync(Cancel);
        return app;
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

    private static async Task<RealtimeMessage<T>> FirstAsync<T>(IAsyncEnumerable<RealtimeMessage<T>> source)
    {
        await foreach (var message in source)
        {
            return message;
        }

        throw new InvalidOperationException("The subscription ended.");
    }
}
