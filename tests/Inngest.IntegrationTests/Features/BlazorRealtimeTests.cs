using Bunit;
using Bunit.Extensions.WaitForHelpers;
using Inngest.IntegrationTests.Evaluation.Functions;
using Inngest.IntegrationTests.Evaluation.Harness;
using Inngest.IntegrationTests.Harness;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Inngest.IntegrationTests.Features;

// RealtimeView against the dev server, rendered by bUnit: on the server, where it reads Inngest through the app's own
// IInngestRealtime, and as WebAssembly runs it, through the relay source and a MapInngestRealtime endpoint that
// relays the dev server.
public sealed class BlazorRealtimeTests(EvaluationEnvironment environment) : EvaluationScenario<FeaturesFixture>(environment)
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(30);

    private IInngestRealtime Realtime => Fixture.App.Services.GetRequiredService<IInngestRealtime>();

    [Fact(Timeout = 90_000)]
    public async Task A_view_on_the_server_shows_the_typed_data_a_function_publishes()
    {
        var topic = FeatureFunctions.Status(TestId);
        await using var context = ServerContext();
        var cut = context.Render<RealtimeView<Progress>>(parameters => parameters.Add(view => view.Topic, topic).Add(view => view.ChildContent, ShowStatus));
        await WaitUntilShownAsync(cut, "ready 0", () => Realtime.PublishAsync(topic, new Progress("ready", 0), Cancellation));

        var run = await RunToEndAsync(FeatureFunctions.TypedPublish, new RunEvent(TestId));

        run.Status.ShouldBe(RunStatus.Completed);
        cut.WaitForAssertion(() => cut.Markup.ShouldBe("Open: Packed 50"), Wait);
    }

    [Fact(Timeout = 90_000)]
    public async Task A_view_on_the_server_shows_the_text_a_function_streams()
    {
        var tokens = FeatureFunctions.Tokens(TestId);
        var topic = new RealtimeTopic<string>(tokens.Channel, tokens.Name);
        await using var context = ServerContext();
        var cut = context.Render<RealtimeView<string>>(parameters => parameters.Add(view => view.Topic, topic).Add(view => view.ChildContent, ShowText));
        await WaitUntilShownAsync(cut, "ready|", () => Realtime.PublishAsync(topic, "ready", Cancellation));

        var run = await RunToEndAsync(FeatureFunctions.Streamed, new RunEvent(TestId));

        run.Status.ShouldBe(RunStatus.Completed);
        cut.WaitForAssertion(() => cut.Markup.ShouldBe("|Your order is on its way 😀"), Wait);
    }

    [Fact(Timeout = 90_000)]
    public async Task A_view_in_WebAssembly_shows_the_typed_data_a_function_publishes_through_the_relay()
    {
        var topic = FeatureFunctions.Status(TestId);
        await using var relay = await StartRelayAsync();
        await using var context = RelayContext(relay);
        var cut = context.Render<RealtimeView<Progress>>(parameters => parameters.Add(view => view.Topic, topic).Add(view => view.ChildContent, ShowStatus));
        await WaitUntilShownAsync(cut, "ready 0", () => Realtime.PublishAsync(topic, new Progress("ready", 0), Cancellation));

        var run = await RunToEndAsync(FeatureFunctions.TypedPublish, new RunEvent(TestId));

        run.Status.ShouldBe(RunStatus.Completed);
        cut.WaitForAssertion(() => cut.Markup.ShouldBe("Open: Packed 50"), Wait);
    }

    [Fact(Timeout = 90_000)]
    public async Task A_view_in_WebAssembly_shows_the_text_a_function_streams_through_the_relay()
    {
        var tokens = FeatureFunctions.Tokens(TestId);
        var topic = new RealtimeTopic<string>(tokens.Channel, tokens.Name);
        await using var relay = await StartRelayAsync();
        await using var context = RelayContext(relay);
        var cut = context.Render<RealtimeView<string>>(parameters => parameters.Add(view => view.Topic, topic).Add(view => view.ChildContent, ShowText));
        await WaitUntilShownAsync(cut, "ready|", () => Realtime.PublishAsync(topic, "ready", Cancellation));

        var run = await RunToEndAsync(FeatureFunctions.Streamed, new RunEvent(TestId));

        run.Status.ShouldBe(RunStatus.Completed);
        cut.WaitForAssertion(() => cut.Markup.ShouldBe("|Your order is on its way 😀"), Wait);
    }

    private static RenderFragment<RealtimeState<Progress>> ShowStatus => state => builder =>
        builder.AddContent(0, $"{state.Connection}: {state.Latest?.Data?.Text} {state.Latest?.Data?.Percent}");

    // The marker is the data message, which the end of the stream follows; the text is the stream's.
    private static RenderFragment<RealtimeState<string>> ShowText => state => builder =>
        builder.AddContent(0, $"{state.Latest?.Data}|{state.TextByTopic.GetValueOrDefault("tokens")}");

    // The server project of an app: components read Inngest with the app's own realtime.
    private BunitContext ServerContext()
    {
        var context = new BunitContext();
        context.Services.AddSingleton(Realtime);
        context.Services.AddInngestRealtimeServerSource();
        return context;
    }

    // The client project of an app: components read the relay through an HttpClient, here on an in-memory server.
    private static BunitContext RelayContext(WebApplication relay)
    {
        var context = new BunitContext();
        context.Services
            .AddInngestRealtimeRelaySource(options => options.Url = (channel, _) => new Uri($"http://relay.test/realtime/{Uri.EscapeDataString(channel)}"))
            .ConfigurePrimaryHttpMessageHandler(() => relay.GetTestServer().CreateHandler());
        return context;
    }

    // Inngest delivers only to subscribers connected when a message is published, so publish a marker until the view shows it.
    private static async Task WaitUntilShownAsync<T>(IRenderedComponent<RealtimeView<T>> cut, string marker, Func<Task> publishMarker)
    {
        for (var attempt = 0; attempt < 50; attempt++)
        {
            await publishMarker();
            try
            {
                cut.WaitForAssertion(() => cut.Markup.ShouldEndWith(marker, Case.Sensitive), TimeSpan.FromMilliseconds(500));
                return;
            }
            catch (WaitForFailedException)
            {
            }
        }

        throw new TimeoutException($"The view did not show \"{marker}\", so its subscription did not receive the marker.");
    }

    // An app of its own that relays the dev server's realtime to browsers.
    private async Task<WebApplication> StartRelayAsync()
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
        app.MapInngestRealtime("/realtime/{channel}", context =>
            ValueTask.FromResult<RealtimeSubscription?>(new((string)context.Request.RouteValues["channel"]!, ["status", "tokens"])));
        await app.StartAsync(Cancellation);
        return app;
    }
}
