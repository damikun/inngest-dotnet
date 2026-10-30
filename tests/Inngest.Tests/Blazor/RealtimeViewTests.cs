using System.Text.Json;
using Bunit;
using Inngest.Tests.Execution;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Inngest.Tests.Blazor;

// RealtimeView in bUnit's renderer, which is interactive, and in the static renderer prerendering uses, with a source
// whose subscriptions the test controls.
public sealed class RealtimeViewTests : BunitContext
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);
    private static readonly RealtimeTopic<Progress> Status = new("order:7", "status");

    private readonly FakeRealtimeSource _source = new();

    public RealtimeViewTests()
    {
        Services.AddSingleton<IRealtimeSource>(_source);
        Services.AddFakeLogging();
    }

    private static CancellationToken Cancel => Xunit.TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_view_that_is_prerendered_does_not_subscribe()
    {
        await using var services = new ServiceCollection().AddLogging().AddSingleton<IRealtimeSource>(_source).BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());

        var html = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var output = await renderer.RenderComponentAsync<RealtimeView<Progress>>(ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                [nameof(RealtimeView<Progress>.Topic)] = Status,
                [nameof(RealtimeView<Progress>.ChildContent)] = Describe(),
            }));
            return output.ToHtmlString();
        });
        await Task.Delay(100, Cancel);

        html.ShouldContain("<p class=\"connection\">Idle</p>");
        _source.Subscriptions.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_view_that_was_prerendered_is_disposed_after_the_scope_of_its_request()
    {
        // The end of a request disposes its scope, which holds the renderer, so the renderer disposes its components
        // when the services are already gone. This server only prerenders the view, so it registers no source for it.
        var provider = new ServiceCollection().AddLogging().BuildServiceProvider();
        var scope = provider.CreateAsyncScope();
        var renderer = new HtmlRenderer(scope.ServiceProvider, scope.ServiceProvider.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var output = await renderer.RenderComponentAsync<RealtimeView<Progress>>(ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                [nameof(RealtimeView<Progress>.Topic)] = Status,
            }));
            return output.ToHtmlString();
        });

        await scope.DisposeAsync();
        await renderer.DisposeAsync();

        await provider.DisposeAsync();
    }

    [Fact]
    public async Task A_view_without_a_registered_source_fails_instead_of_failing_the_page()
    {
        Services.RemoveAll<IRealtimeSource>();

        var cut = RenderStatus();

        cut.WaitForAssertion(() => cut.Find(".connection").TextContent.ShouldBe("Failed"), Wait);
        cut.Find(".error").TextContent.ShouldContain(nameof(IRealtimeSource));
        await Task.CompletedTask;
    }

    [Fact]
    public async Task The_first_interactive_render_subscribes_once()
    {
        var cut = RenderStatus();

        await SubscribedAsync(1);
        _source.Subscriptions[0].Channel.ShouldBe("order:7");
        _source.Subscriptions[0].Topics.ShouldBe(["status"]);
        cut.WaitForAssertion(() => cut.Find(".connection").TextContent.ShouldBe("Connecting"), Wait);

        // The parent renders it again with a topic of the same value.
        cut.Render(parameters => parameters.Add(view => view.Topic, new RealtimeTopic<Progress>("order:7", "status")));
        await Task.Delay(100, Cancel);
        _source.Subscriptions.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Messages_are_rendered_as_they_arrive_and_the_connection_is_open_once_one_has()
    {
        var cut = RenderStatus();
        await SubscribedAsync(1);
        var subscription = _source.Subscription<Progress>(0);

        subscription.Send("status", new Progress("Received", 10));
        subscription.Send("status", new Progress("Packed", 50));

        cut.WaitForAssertion(() => cut.FindAll("li").Select(item => item.TextContent).ShouldBe(["status: Received 10", "status: Packed 50"]), Wait);
        cut.Find(".latest").TextContent.ShouldBe("status: Packed 50");
        cut.Find(".connection").TextContent.ShouldBe("Open");
    }

    [Fact]
    public async Task The_latest_message_of_each_topic_is_kept_apart_from_the_history()
    {
        var cut = Render<RealtimeView<JsonElement>>(parameters => parameters
            .Add(view => view.Channel, "order:7")
            .Add(view => view.Topics, new[] { "status", "audit" })
            .Add(view => view.MaxMessages, 1)
            .Add(view => view.ChildContent, state => builder =>
            {
                builder.OpenElement(0, "p");
                builder.AddContent(1, string.Join(",", state.LatestByTopic.OrderBy(pair => pair.Key).Select(pair => $"{pair.Key}={pair.Value.Data.GetString()}")));
                builder.CloseElement();
                builder.OpenElement(2, "i");
                builder.AddContent(3, state.Messages.Count);
                builder.CloseElement();
            }));
        await SubscribedAsync(1);
        var subscription = _source.Subscription<JsonElement>(0);

        subscription.Send("status", JsonSerializer.SerializeToElement("one"));
        subscription.Send("audit", JsonSerializer.SerializeToElement("two"));
        subscription.Send("status", JsonSerializer.SerializeToElement("three"));

        cut.WaitForAssertion(() => cut.Find("p").TextContent.ShouldBe("audit=two,status=three"), Wait);
        cut.Find("i").TextContent.ShouldBe("1");
    }

    [Fact]
    public async Task Only_the_newest_messages_up_to_MaxMessages_are_kept_and_a_smaller_limit_trims_them()
    {
        var cut = RenderStatus(maxMessages: 3);
        await SubscribedAsync(1);
        var subscription = _source.Subscription<Progress>(0);

        for (var percent = 1; percent <= 5; percent++)
        {
            subscription.Send("status", new Progress("Step", percent));
        }

        cut.WaitForAssertion(() => cut.FindAll("li").Select(item => item.TextContent).ShouldBe(["status: Step 3", "status: Step 4", "status: Step 5"]), Wait);

        cut.Render(parameters => parameters.Add(view => view.MaxMessages, 2));
        cut.FindAll("li").Select(item => item.TextContent).ShouldBe(["status: Step 4", "status: Step 5"]);
        subscription.Send("status", new Progress("Step", 6));
        cut.WaitForAssertion(() => cut.FindAll("li").Select(item => item.TextContent).ShouldBe(["status: Step 5", "status: Step 6"]), Wait);
        cut.Find(".latest").TextContent.ShouldBe("status: Step 6");
    }

    [Fact]
    public async Task The_text_of_a_stream_grows_with_its_chunks_and_starts_over_with_the_next_stream()
    {
        var tokens = new RealtimeTopic<string>("order:7", "tokens");
        var cut = Render<RealtimeView<string>>(parameters => parameters
            .Add(view => view.Topic, tokens)
            .Add(view => view.MaxMessages, 0)
            .Add(view => view.ChildContent, state => builder =>
            {
                builder.OpenElement(0, "p");
                builder.AddContent(1, state.TextByTopic.GetValueOrDefault("tokens", "none"));
                builder.CloseElement();
            }));
        await SubscribedAsync(1);
        var subscription = _source.Subscription<string>(0);

        subscription.Send(Stream(RealtimeMessageKind.StreamStart));
        subscription.Send(Stream(RealtimeMessageKind.StreamChunk, "Your order "));
        subscription.Send(Stream(RealtimeMessageKind.StreamChunk, "is packed"));
        subscription.Send(Stream(RealtimeMessageKind.StreamEnd));

        cut.WaitForAssertion(() => cut.Find("p").TextContent.ShouldBe("Your order is packed"), Wait);

        subscription.Send(Stream(RealtimeMessageKind.StreamStart, streamId: "s2"));
        subscription.Send(Stream(RealtimeMessageKind.StreamChunk, "Shipped", streamId: "s2"));

        cut.WaitForAssertion(() => cut.Find("p").TextContent.ShouldBe("Shipped"), Wait);

        RealtimeMessage<string> Stream(RealtimeMessageKind kind, string? text = null, string streamId = "s1") =>
            new(kind, "order:7", "tokens") { StreamId = streamId, Text = text };
    }

    [Fact]
    public async Task Another_topic_subscribes_again_and_forgets_what_the_old_one_sent()
    {
        // The old subscription goes on delivering after it is cancelled, as a source does for a moment.
        _source.IgnoreCancellation = true;
        var cut = RenderStatus();
        await SubscribedAsync(1);
        var old = _source.Subscription<Progress>(0);
        _source.IgnoreCancellation = false;
        old.Send("status", new Progress("Packed", 50));
        cut.WaitForAssertion(() => cut.Find(".latest").TextContent.ShouldBe("status: Packed 50"), Wait);

        cut.Render(parameters => parameters.Add(view => view.Topic, new RealtimeTopic<Progress>("order:8", "status")));

        await SubscribedAsync(2);
        old.Token.IsCancellationRequested.ShouldBeTrue();
        _source.Subscriptions[1].Channel.ShouldBe("order:8");
        cut.Find(".latest").TextContent.ShouldBe("none");
        cut.Find(".connection").TextContent.ShouldBe("Connecting");
        old.Send("status", new Progress("Late", 99));
        _source.Subscription<Progress>(1).Send("status", new Progress("Received", 10));

        cut.WaitForAssertion(() => cut.Find(".latest").TextContent.ShouldBe("status: Received 10"), Wait);
        cut.FindAll("li").Count.ShouldBe(1);
        old.Complete();
    }

    [Fact]
    public async Task A_channel_with_topics_of_the_same_values_does_not_subscribe_again_and_other_topics_do()
    {
        var cut = Render<RealtimeView<JsonElement>>(parameters => parameters
            .Add(view => view.Channel, "order:7")
            .Add(view => view.Topics, new[] { "status", "audit" }));
        await SubscribedAsync(1);

        cut.Render(parameters => parameters.Add(view => view.Topics, new[] { "status", "audit" }));
        await Task.Delay(100, Cancel);
        _source.Subscriptions.Count.ShouldBe(1);

        cut.Render(parameters => parameters.Add(view => view.Topics, new[] { "status" }));

        await SubscribedAsync(2);
        _source.Subscriptions[0].Token.IsCancellationRequested.ShouldBeTrue();
        _source.Subscriptions[1].Topics.ShouldBe(["status"]);
    }

    [Fact]
    public async Task A_view_with_nothing_to_watch_yet_subscribes_when_it_is_given_something()
    {
        var cut = Render<RealtimeView<Progress>>(parameters => parameters.Add(view => view.ChildContent, Describe()));
        await Task.Delay(100, Cancel);
        _source.Subscriptions.ShouldBeEmpty();
        cut.Find(".connection").TextContent.ShouldBe("Idle");

        cut.Render(parameters => parameters.Add(view => view.Topic, Status));

        await SubscribedAsync(1);
        cut.WaitForAssertion(() => cut.Find(".connection").TextContent.ShouldBe("Connecting"), Wait);

        cut.Render(parameters => parameters.Add(view => view.Topic, null));

        cut.WaitForAssertion(() => cut.Find(".connection").TextContent.ShouldBe("Idle"), Wait);
        _source.Subscriptions[0].Token.IsCancellationRequested.ShouldBeTrue();
    }

    [Fact]
    public async Task Disposing_the_view_cancels_the_subscription_and_ends_the_enumeration()
    {
        var cut = RenderStatus();
        await SubscribedAsync(1);
        var subscription = _source.Subscription<Progress>(0);

        await DisposeComponentsAsync();

        await subscription.Ended.WaitAsync(Wait, Cancel);
        subscription.Token.IsCancellationRequested.ShouldBeTrue();
        cut.IsDisposed.ShouldBeTrue();
    }

    [Fact]
    public async Task What_a_disposed_view_still_receives_is_dropped_without_an_error()
    {
        _source.IgnoreCancellation = true;
        var received = new List<RealtimeMessage<Progress>>();
        var cut = RenderStatus(onMessage: received.Add);
        await SubscribedAsync(1);
        var subscription = _source.Subscription<Progress>(0);

        var disposing = DisposeComponentsAsync();
        subscription.Send("status", new Progress("Late", 99));
        subscription.Fail(new InvalidOperationException("The source broke after the view was gone."));
        await disposing.WaitAsync(Wait, Cancel);

        // Rendering anything else throws what the renderer was told about, if it was told.
        Render<RealtimeView<Progress>>(parameters => parameters.Add(view => view.ChildContent, Describe()));
        received.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_view_does_not_wait_for_ever_for_a_source_that_ignores_cancellation()
    {
        var time = new TestTimeProvider(EngineHost.Now);
        Services.AddSingleton<TimeProvider>(time);
        _source.IgnoreCancellation = true;
        var cut = RenderStatus();
        await SubscribedAsync(1);

        var disposing = Renderer.Dispatcher.InvokeAsync(() => cut.Instance.DisposeAsync().AsTask());
        await time.TimersCreated(1).WaitAsync(Wait, Cancel);
        disposing.IsCompleted.ShouldBeFalse();
        time.Advance(TimeSpan.FromSeconds(6));

        await disposing.WaitAsync(Wait, Cancel);
        _source.Subscription<Progress>(0).Complete();
    }

    [Fact]
    public async Task A_subscription_that_fails_shows_in_the_state_and_does_not_reach_the_renderer()
    {
        var cut = RenderStatus();
        await SubscribedAsync(1);
        var failure = new InngestException("Failed to get a subscription token for channel \"order:7\": 401 Unauthorized", 401);

        _source.Subscription<Progress>(0).Fail(failure);

        cut.WaitForAssertion(() => cut.Find(".connection").TextContent.ShouldBe("Failed"), Wait);
        cut.Find(".error").TextContent.ShouldBe(failure.Message);
        Services.GetFakeLogCollector().GetSnapshot().ShouldContain(record => record.Level == LogLevel.Warning && record.Exception == failure);
    }

    [Fact]
    public async Task A_source_that_refuses_the_arguments_fails_the_view_the_same_way()
    {
        _source.SubscribeFailure = new ArgumentException("The channel is empty.");

        var cut = RenderStatus();

        cut.WaitForAssertion(() => cut.Find(".error").TextContent.ShouldBe("The channel is empty."), Wait);
        cut.Find(".connection").TextContent.ShouldBe("Failed");
        await Task.CompletedTask;
    }

    [Fact]
    public async Task ThrowOnError_hands_a_failure_to_the_error_boundary()
    {
        var cut = Render<ErrorBoundary>(parameters => parameters
            .AddChildContent<RealtimeView<Progress>>(view => view
                .Add(v => v.Topic, Status)
                .Add(v => v.ThrowOnError, true)
                .Add(v => v.ChildContent, Describe()))
            .Add(boundary => boundary.ErrorContent, error => $"<p class=\"boundary\">{error.Message}</p>"));
        await SubscribedAsync(1);

        _source.Subscription<Progress>(0).Fail(new InngestException("Inngest refused the subscription.", 401));

        cut.WaitForAssertion(() => cut.Find(".boundary").TextContent.ShouldBe("Inngest refused the subscription."), Wait);
        cut.FindAll(".connection").ShouldBeEmpty();
    }

    [Fact]
    public async Task Restart_subscribes_again_after_a_failure_with_the_messages_cleared()
    {
        var cut = RenderStatus();
        await SubscribedAsync(1);
        _source.Subscription<Progress>(0).Send("status", new Progress("Packed", 50));
        cut.WaitForAssertion(() => cut.FindAll("li").Count.ShouldBe(1), Wait);
        _source.Subscription<Progress>(0).Fail(new InngestException("Failed to subscribe.", 503));
        cut.WaitForAssertion(() => cut.Find(".connection").TextContent.ShouldBe("Failed"), Wait);

        await Renderer.Dispatcher.InvokeAsync(cut.Instance.Restart);

        await SubscribedAsync(2);
        cut.Find(".connection").TextContent.ShouldBe("Connecting");
        cut.FindAll("li").ShouldBeEmpty();
        cut.FindAll(".error").Single().TextContent.ShouldBeEmpty();
        _source.Subscription<Progress>(1).Send("status", new Progress("Shipped", 100));
        cut.WaitForAssertion(() => cut.Find(".latest").TextContent.ShouldBe("status: Shipped 100"), Wait);
    }

    [Fact]
    public async Task A_source_can_say_it_is_reconnecting()
    {
        var cut = RenderStatus();
        await SubscribedAsync(1);
        var subscription = _source.Subscription<Progress>(0);

        subscription.Report(RealtimeConnectionState.Open);
        cut.WaitForAssertion(() => cut.Find(".connection").TextContent.ShouldBe("Open"), Wait);
        subscription.Report(RealtimeConnectionState.Reconnecting);
        cut.WaitForAssertion(() => cut.Find(".connection").TextContent.ShouldBe("Reconnecting"), Wait);

        // A message proves the connection is back, whatever the source said.
        subscription.Send("status", new Progress("Packed", 50));
        cut.WaitForAssertion(() => cut.Find(".connection").TextContent.ShouldBe("Open"), Wait);
    }

    [Fact]
    public async Task A_subscription_the_source_ends_closes_the_view()
    {
        var cut = RenderStatus();
        await SubscribedAsync(1);
        _source.Subscription<Progress>(0).Send("status", new Progress("Packed", 50));

        _source.Subscription<Progress>(0).Complete();

        cut.WaitForAssertion(() => cut.Find(".connection").TextContent.ShouldBe("Closed"), Wait);
        cut.Find(".latest").TextContent.ShouldBe("status: Packed 50");
    }

    [Fact]
    public async Task OnMessage_gets_each_message_in_order_after_the_state_has_it()
    {
        var seen = new List<string>();
        RealtimeState<Progress>? state = null;
        RenderStatus(
            onMessage: message =>
            {
                lock (seen)
                {
                    seen.Add($"{message.Data?.Percent}, with {state!.Messages.Count} in the state");
                }
            },
            content: current =>
            {
                state = current;
                return _ => { };
            });
        await SubscribedAsync(1);

        _source.Subscription<Progress>(0).Send("status", new Progress("One", 1));
        _source.Subscription<Progress>(0).Send("status", new Progress("Two", 2));

        // The callback is not followed by a render, which WaitForAssertion needs to look again.
        await UntilAsync(() => { lock (seen) { return seen.Count == 2; } });
        seen.ShouldBe(["1, with 1 in the state", "2, with 2 in the state"]);
    }

    [Fact]
    public async Task An_exception_from_OnMessage_fails_the_subscription()
    {
        var cut = RenderStatus(onMessage: _ => throw new InvalidOperationException("The handler broke."));
        await SubscribedAsync(1);

        _source.Subscription<Progress>(0).Send("status", new Progress("One", 1));

        cut.WaitForAssertion(() => cut.Find(".error").TextContent.ShouldBe("The handler broke."), Wait);
        cut.Find(".connection").TextContent.ShouldBe("Failed");
        await _source.Subscriptions[0].Ended.WaitAsync(Wait, Cancel);
    }

    [Fact]
    public void A_topic_and_a_channel_together_are_refused()
    {
        var exception = Should.Throw<InvalidOperationException>(() => Render<RealtimeView<Progress>>(parameters => parameters
            .Add(view => view.Topic, Status)
            .Add(view => view.Channel, "order:7")));

        exception.Message.ShouldContain("not both");
    }

    [Fact]
    public void A_channel_without_topics_is_refused()
    {
        var exception = Should.Throw<InvalidOperationException>(() => Render<RealtimeView<JsonElement>>(parameters => parameters
            .Add(view => view.Channel, "order:7")));

        exception.Message.ShouldContain("needs both");
    }

    [Fact]
    public void A_channel_gives_JsonElement_so_other_types_are_refused()
    {
        var exception = Should.Throw<InvalidOperationException>(() => Render<RealtimeView<Progress>>(parameters => parameters
            .Add(view => view.Channel, "order:7")
            .Add(view => view.Topics, new[] { "status" })));

        exception.Message.ShouldContain("JsonElement");
    }

    [Fact]
    public void A_negative_MaxMessages_is_refused() =>
        Should.Throw<ArgumentOutOfRangeException>(() => RenderStatus(maxMessages: -1));

    private IRenderedComponent<RealtimeView<Progress>> RenderStatus(
        int maxMessages = 100,
        Action<RealtimeMessage<Progress>>? onMessage = null,
        RenderFragment<RealtimeState<Progress>>? content = null) =>
        Render<RealtimeView<Progress>>(parameters =>
        {
            parameters
                .Add(view => view.Topic, Status)
                .Add(view => view.MaxMessages, maxMessages)
                .Add(view => view.ChildContent, content ?? Describe());
            if (onMessage is not null)
            {
                parameters.Add(view => view.OnMessage, onMessage);
            }
        });

    // Waits for the view to have subscribed this many times, which it does after its first render.
    private Task SubscribedAsync(int count) => UntilAsync(() => _source.Subscriptions.Count >= count);

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

    // What the view's content shows of its state.
    private static RenderFragment<RealtimeState<Progress>> Describe() => state => builder =>
    {
        builder.OpenElement(0, "p");
        builder.AddAttribute(1, "class", "connection");
        builder.AddContent(2, state.Connection.ToString());
        builder.CloseElement();
        builder.OpenElement(3, "p");
        builder.AddAttribute(4, "class", "error");
        builder.AddContent(5, state.Error?.Message);
        builder.CloseElement();
        builder.OpenElement(6, "p");
        builder.AddAttribute(7, "class", "latest");
        builder.AddContent(8, state.Latest is { } latest ? Show(latest) : "none");
        builder.CloseElement();
        builder.OpenElement(9, "ul");
        foreach (var message in state.Messages)
        {
            builder.OpenElement(10, "li");
            builder.AddContent(11, Show(message));
            builder.CloseElement();
        }

        builder.CloseElement();
    };

    private static string Show(RealtimeMessage<Progress> message) => $"{message.Topic}: {message.Data?.Text} {message.Data?.Percent}";

    private sealed record Progress(string Text, int Percent);
}
