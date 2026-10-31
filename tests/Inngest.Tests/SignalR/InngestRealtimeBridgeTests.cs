using System.Collections.Concurrent;
using Inngest.Tests.Realtime;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;

namespace Inngest.Tests.SignalR;

// The bridge over a fake hub lifetime manager and scripted subscriptions to Inngest: who shares a subscription, when it
// closes, what a slow client costs the others, and that instances on a backplane deliver once.
public sealed class InngestRealtimeBridgeTests : IDisposable
{
    private const string Order = "order:o-1";

    private readonly BridgeRig _rig = new();

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    public void Dispose() => _rig.Dispose();

    [Fact]
    public async Task Clients_that_join_the_same_topics_share_one_subscription_and_receive_Inngests_messages_as_they_are()
    {
        var app = _rig.AddInstance();
        var feed = new SseFeed();
        app.Api.Answer(feed);

        var joined = await app.Bridge.SubscribeAsync(new FakeCaller("c1"), Order, ["status"], Cancel);
        await app.Bridge.SubscribeAsync(new FakeCaller("c2"), Order, ["status"], Cancel);
        const string Message = """{"kind":"data", "data":{"text":"Packed"},"channel":"order:o-1","topic":"status"}""";
        await feed.SendAsync(Message);

        await Wait.UntilAsync(() => _rig.Hubs.Messages("c1").Count == 1 && _rig.Hubs.Messages("c2").Count == 1);
        _rig.Hubs.Messages("c1").ShouldBe([Message]);
        _rig.Hubs.Messages("c2").ShouldBe([Message]);
        app.Api.TokensMinted.ShouldBe(1);
        joined.Channel.ShouldBe(Order);
        joined.Topics.ShouldBe(["status"]);
    }

    [Fact]
    public async Task The_same_topics_in_any_order_are_one_subscription_and_other_topics_are_another()
    {
        var app = _rig.AddInstance();
        app.Api.Answer(new SseFeed());
        app.Api.Answer(new SseFeed());

        await app.Bridge.SubscribeAsync(new FakeCaller("c1"), Order, ["tokens", "status"], Cancel);
        await app.Bridge.SubscribeAsync(new FakeCaller("c2"), Order, ["status", "tokens", "status"], Cancel);
        await app.Bridge.SubscribeAsync(new FakeCaller("c3"), Order, ["status"], Cancel);
        await app.Bridge.SubscribeAsync(new FakeCaller("c4"), "order:o-2", ["status"], Cancel);

        await Wait.UntilAsync(() => app.Recorded.Tokens.Count == 3);
        app.Bridge.FeedCount.ShouldBe(3);
        app.Recorded.Tokens.Select(token => $"{token.Channel} {string.Join(',', token.Topics)}").Order()
            .ShouldBe([$"{Order} status", $"{Order} status,tokens", "order:o-2 status"]);
    }

    [Fact]
    public async Task Names_that_contain_separators_do_not_share_a_subscription_with_another_channel()
    {
        var app = _rig.AddInstance();
        app.Api.Answer(new SseFeed());
        app.Api.Answer(new SseFeed());

        await app.Bridge.SubscribeAsync(new FakeCaller("c1"), "a", ["b\nc"], Cancel);
        await app.Bridge.SubscribeAsync(new FakeCaller("c2"), "a\nb", ["c"], Cancel);
        await app.Bridge.SubscribeAsync(new FakeCaller("c3"), "a;1:b", ["c"], Cancel);

        app.Bridge.FeedCount.ShouldBe(3);
    }

    [Fact]
    public async Task The_subscription_closes_when_the_last_client_leaves()
    {
        var app = _rig.AddInstance();
        app.Api.Answer(new SseFeed());
        var first = new FakeCaller("c1");
        var second = new FakeCaller("c2");
        await app.Bridge.SubscribeAsync(first, Order, ["status"], Cancel);
        await app.Bridge.SubscribeAsync(second, Order, ["status"], Cancel);
        await Wait.UntilAsync(() => app.Subscriptions.Count == 1);

        app.Bridge.Unsubscribe(first, Order, ["status"]);
        await Task.Delay(100, Cancel);
        app.Subscriptions[0].IsCancellationRequested.ShouldBeFalse();
        app.Bridge.Unsubscribe(second, Order, ["status"]);

        await Wait.UntilAsync(() => app.Subscriptions[0].IsCancellationRequested);
        app.Bridge.FeedCount.ShouldBe(0);
    }

    [Fact]
    public async Task The_subscription_closes_when_the_last_client_disconnects_and_nothing_is_left_behind()
    {
        var app = _rig.AddInstance();
        app.Api.Answer(new SseFeed());
        var first = new FakeCaller("c1");
        var second = new FakeCaller("c2");
        await app.Bridge.SubscribeAsync(first, Order, ["status"], Cancel);
        await app.Bridge.SubscribeAsync(second, Order, ["status"], Cancel);
        await Wait.UntilAsync(() => app.Subscriptions.Count == 1);

        first.Disconnect();
        await Wait.UntilAsync(() => app.Bridge.ListenerCount == 1);
        app.Subscriptions[0].IsCancellationRequested.ShouldBeFalse();
        second.Disconnect();

        await Wait.UntilAsync(() => app.Subscriptions[0].IsCancellationRequested);
        app.Bridge.FeedCount.ShouldBe(0);
        app.Bridge.ListenerCount.ShouldBe(0);
        first.Aborted.ShouldBeFalse();
    }

    [Fact]
    public async Task A_client_that_disconnected_before_joining_leaves_nothing_behind()
    {
        var app = _rig.AddInstance();
        var gone = new FakeCaller("c1");
        gone.Disconnect();

        await app.Bridge.SubscribeAsync(gone, Order, ["status"], Cancel);

        await Wait.UntilAsync(() => app.Bridge.FeedCount == 0 && app.Bridge.ListenerCount == 0);
    }

    [Fact]
    public async Task Joining_and_leaving_are_idempotent()
    {
        var app = _rig.AddInstance();
        var feed = new SseFeed();
        app.Api.Answer(feed);
        var caller = new FakeCaller("c1");
        var other = new FakeCaller("c2");
        await app.Bridge.SubscribeAsync(other, Order, ["status"], Cancel);

        await app.Bridge.SubscribeAsync(caller, Order, ["status"], Cancel);
        await app.Bridge.SubscribeAsync(caller, Order, ["status"], Cancel);
        await feed.SendAsync("""{"n":1}""");
        await Wait.UntilAsync(() => _rig.Hubs.Messages("c1").Count == 1 && _rig.Hubs.Messages("c2").Count == 1);
        app.Bridge.Unsubscribe(caller, Order, ["status"]);
        app.Bridge.Unsubscribe(caller, Order, ["status"]);
        app.Bridge.Unsubscribe(caller, "order:never-joined", ["status"]);
        app.Bridge.Unsubscribe(new FakeCaller("c3"), Order, ["status"]);
        await feed.SendAsync("""{"n":2}""");

        await Wait.UntilAsync(() => _rig.Hubs.Messages("c2").Count == 2);
        _rig.Hubs.Messages("c1").ShouldBe(["""{"n":1}"""]);
        _rig.Hubs.Messages("c2").ShouldBe(["""{"n":1}""", """{"n":2}"""]);
        app.Api.TokensMinted.ShouldBe(1);
    }

    [Fact]
    public async Task A_client_joins_again_after_it_left_and_the_subscription_opens_anew()
    {
        var app = _rig.AddInstance();
        var first = new SseFeed();
        var second = new SseFeed();
        app.Api.Answer(first);
        app.Api.Answer(second);
        var caller = new FakeCaller("c1");
        await app.Bridge.SubscribeAsync(caller, Order, ["status"], Cancel);
        await Wait.UntilAsync(() => app.Subscriptions.Count == 1);
        app.Bridge.Unsubscribe(caller, Order, ["status"]);
        await Wait.UntilAsync(() => app.Subscriptions[0].IsCancellationRequested);

        await app.Bridge.SubscribeAsync(caller, Order, ["status"], Cancel);
        await second.SendAsync("""{"n":2}""");

        await Wait.UntilAsync(() => _rig.Hubs.Messages("c1").Count == 1);
        _rig.Hubs.Messages("c1").ShouldBe(["""{"n":2}"""]);
        app.Subscriptions[1].IsCancellationRequested.ShouldBeFalse();
    }

    [Fact]
    public async Task Messages_arrive_in_the_order_Inngest_sent_them()
    {
        var app = _rig.AddInstance();
        var feed = new SseFeed();
        app.Api.Answer(feed);
        await app.Bridge.SubscribeAsync(new FakeCaller("c1"), Order, ["status"], Cancel);
        await app.Bridge.SubscribeAsync(new FakeCaller("c2"), Order, ["status"], Cancel);
        string[] expected = [.. Enumerable.Range(0, 500).Select(n => $$"""{"n":{{n}}}""")];

        foreach (var message in expected)
        {
            await feed.SendAsync(message);
        }

        await Wait.UntilAsync(() => _rig.Hubs.Messages("c1").Count == 500 && _rig.Hubs.Messages("c2").Count == 500);
        _rig.Hubs.Messages("c1").ShouldBe(expected);
        _rig.Hubs.Messages("c2").ShouldBe(expected);
    }

    [Fact]
    public async Task A_refused_join_throws_a_HubException_and_opens_nothing()
    {
        var app = _rig.AddInstance(_ => ValueTask.FromResult<RealtimeSubscription?>(null));

        var exception = await Should.ThrowAsync<HubException>(() => app.Bridge.SubscribeAsync(new FakeCaller("c1"), Order, ["status"], Cancel).AsTask());

        exception.Message.ShouldBe("You may not subscribe to this channel.");
        app.Bridge.FeedCount.ShouldBe(0);
        app.Bridge.ListenerCount.ShouldBe(0);
        app.Api.TokensMinted.ShouldBe(0);
    }

    [Fact]
    public async Task The_callback_sees_who_asks_and_what_for_and_a_HubException_it_throws_reaches_the_client()
    {
        RealtimeSubscribeRequest? seen = null;
        var app = _rig.AddInstance(request =>
        {
            seen = request;
            throw new HubException("That order is not yours.");
        });
        var caller = new FakeCaller("c1");

        var exception = await Should.ThrowAsync<HubException>(() => app.Bridge.SubscribeAsync(caller, Order, ["status", "tokens"], Cancel).AsTask());

        exception.Message.ShouldBe("That order is not yours.");
        seen.ShouldNotBeNull();
        seen.Caller.ShouldBeSameAs(caller);
        seen.Requested.Channel.ShouldBe(Order);
        seen.Requested.Topics.ShouldBe(["status", "tokens"]);
        app.Bridge.ListenerCount.ShouldBe(0);
    }

    [Fact]
    public async Task Another_exception_of_the_callback_is_not_turned_into_a_HubException()
    {
        var app = _rig.AddInstance(_ => throw new InvalidOperationException("The database is down."));

        await Should.ThrowAsync<InvalidOperationException>(() => app.Bridge.SubscribeAsync(new FakeCaller("c1"), Order, ["status"], Cancel).AsTask());

        app.Bridge.ListenerCount.ShouldBe(0);
    }

    [Fact]
    public async Task The_callback_resolves_scoped_services_from_a_scope_that_ends_with_it()
    {
        ScopedProbe? probe = null;
        var app = _rig.AddInstance(request =>
        {
            probe = request.Services.GetRequiredService<ScopedProbe>();
            probe.IsDisposed.ShouldBeFalse();
            return ValueTask.FromResult<RealtimeSubscription?>(request.Requested);
        });

        await app.Bridge.SubscribeAsync(new FakeCaller("c1"), Order, ["status"], Cancel);

        probe.ShouldNotBeNull();
        probe.IsDisposed.ShouldBeTrue();
    }

    [Fact]
    public async Task The_callback_can_narrow_the_topics_and_a_client_leaves_with_what_it_asked_for_or_what_it_got()
    {
        var app = _rig.AddInstance(request => ValueTask.FromResult<RealtimeSubscription?>(request.Requested with { Topics = [.. request.Requested.Topics.Where(topic => topic != "admin")] }));
        app.Api.Answer(new SseFeed());
        app.Api.Answer(new SseFeed());
        var caller = new FakeCaller("c1");

        var joined = await app.Bridge.SubscribeAsync(caller, Order, ["status", "admin"], Cancel);
        await Wait.UntilAsync(() => app.Subscriptions.Count == 1);
        app.Bridge.Unsubscribe(caller, Order, ["status", "admin"]);
        await Wait.UntilAsync(() => app.Subscriptions[0].IsCancellationRequested);
        await app.Bridge.SubscribeAsync(caller, Order, ["admin", "status"], Cancel);
        await Wait.UntilAsync(() => app.Subscriptions.Count == 2);
        app.Bridge.Unsubscribe(caller, Order, ["status"]);

        joined.Topics.ShouldBe(["status"]);
        app.Recorded.Tokens.ShouldAllBe(token => token.Channel == Order && token.Topics.SequenceEqual(new[] { "status" }));
        await Wait.UntilAsync(() => app.Subscriptions[1].IsCancellationRequested);
        app.Bridge.FeedCount.ShouldBe(0);
    }

    [Fact]
    public async Task The_callback_can_derive_the_channel_from_the_user()
    {
        var app = _rig.AddInstance(request => ValueTask.FromResult<RealtimeSubscription?>(new($"user:{request.Caller.ConnectionId}", ["notifications"])));
        var feed = new SseFeed();
        app.Api.Answer(feed);

        var joined = await app.Bridge.SubscribeAsync(new FakeCaller("c1"), "me", ["anything"], Cancel);
        await feed.SendAsync("""{"kind":"data"}""");

        joined.Channel.ShouldBe("user:c1");
        await Wait.UntilAsync(() => _rig.Hubs.Messages("c1").Count == 1);
        app.Recorded.Tokens.ShouldHaveSingleItem().Channel.ShouldBe("user:c1");
    }

    [Theory]
    [InlineData(null, new[] { "status" })]
    [InlineData("", new[] { "status" })]
    [InlineData(Order, null)]
    [InlineData(Order, new string[0])]
    [InlineData(Order, new[] { "" })]
    [InlineData(Order, new[] { "status", "" })]
    public async Task An_incomplete_request_is_refused_before_the_callback_is_asked(string? channel, string[]? topics)
    {
        var asked = false;
        var app = _rig.AddInstance(request =>
        {
            asked = true;
            return ValueTask.FromResult<RealtimeSubscription?>(request.Requested);
        });

        await Should.ThrowAsync<HubException>(() => app.Bridge.SubscribeAsync(new FakeCaller("c1"), channel!, topics!, Cancel).AsTask());

        asked.ShouldBeFalse();
        app.Bridge.ListenerCount.ShouldBe(0);
    }

    [Fact]
    public async Task A_connection_is_joined_to_at_most_as_many_subscriptions_as_the_options_allow()
    {
        var app = _rig.AddInstance(configure: options => options.MaxSubscriptionsPerConnection = 2);
        app.Api.Answer(new SseFeed());
        app.Api.Answer(new SseFeed());
        var caller = new FakeCaller("c1");
        await app.Bridge.SubscribeAsync(caller, "order:o-1", ["status"], Cancel);
        await app.Bridge.SubscribeAsync(caller, "order:o-2", ["status"], Cancel);

        await app.Bridge.SubscribeAsync(caller, "order:o-2", ["status"], Cancel);
        await Should.ThrowAsync<HubException>(() => app.Bridge.SubscribeAsync(caller, "order:o-3", ["status"], Cancel).AsTask());
        app.Bridge.Unsubscribe(caller, "order:o-1", ["status"]);
        app.Api.Answer(new SseFeed());
        await app.Bridge.SubscribeAsync(caller, "order:o-3", ["status"], Cancel);

        app.Bridge.FeedCount.ShouldBe(2);
    }

    [Fact]
    public async Task A_typed_topic_is_joined_and_left_by_its_channel_and_name()
    {
        var app = _rig.AddInstance();
        var feed = new SseFeed();
        app.Api.Answer(feed);
        var topic = new RealtimeTopic<OrderStatus>(Order, "status");
        var caller = new FakeCaller("c1");

        var joined = await app.Bridge.SubscribeAsync(caller, topic, Cancel);
        await feed.SendAsync("""{"kind":"data","data":{"text":"Packed","percent":50}}""");

        joined.Channel.ShouldBe(Order);
        joined.Topics.ShouldBe(["status"]);
        await Wait.UntilAsync(() => _rig.Hubs.Messages("c1").Count == 1);
        app.Bridge.Unsubscribe(caller, topic);
        await Wait.UntilAsync(() => app.Subscriptions[0].IsCancellationRequested);
    }

    [Fact]
    public async Task Instances_on_a_backplane_deliver_each_message_once_to_each_client()
    {
        var one = _rig.AddInstance();
        var two = _rig.AddInstance();
        var feedOne = new SseFeed();
        var feedTwo = new SseFeed();
        one.Api.Answer(feedOne);
        two.Api.Answer(feedTwo);
        await one.Bridge.SubscribeAsync(new FakeCaller("a1"), Order, ["status"], Cancel);
        await one.Bridge.SubscribeAsync(new FakeCaller("a2"), Order, ["status"], Cancel);
        await two.Bridge.SubscribeAsync(new FakeCaller("b1"), Order, ["status"], Cancel);
        string[] expected = [.. Enumerable.Range(0, 5).Select(n => $$"""{"n":{{n}}}""")];

        // Each instance has its own subscription to Inngest, so each receives every message.
        foreach (var message in expected)
        {
            await feedOne.SendAsync(message);
            await feedTwo.SendAsync(message);
        }

        await Wait.UntilAsync(() => new[] { "a1", "a2", "b1" }.All(id => _rig.Hubs.Messages(id).Count >= 5));
        await Task.Delay(200, Cancel);
        foreach (var id in new[] { "a1", "a2", "b1" })
        {
            _rig.Hubs.Messages(id).ShouldBe(expected);
        }

        // Neither instance sends to a group or to everyone, which on a backplane reaches the clients of the other.
        _rig.Hubs.OtherCalls.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_client_that_falls_behind_loses_its_subscriptions_and_is_told_and_holds_up_neither_the_others_nor_the_reader()
    {
        var app = _rig.AddInstance();
        var feed = new SseFeed();
        app.Api.Answer(feed);
        var slow = new FakeCaller("slow");
        var quick = new FakeCaller("quick");
        _rig.Hubs.Hold("slow");
        await app.Bridge.SubscribeAsync(slow, Order, ["status", "tokens"], Cancel);
        await app.Bridge.SubscribeAsync(quick, Order, ["status", "tokens"], Cancel);
        string[] expected = [.. Enumerable.Range(0, 700).Select(n => $$"""{"n":{{n}}}""")];

        // The first message is under way to the slow client, which holds it up, before the rest come.
        await feed.SendAsync(expected[0]);
        await Wait.UntilAsync(() => _rig.Hubs.IsHeld("slow") && _rig.Hubs.Messages("quick").Count == 1);

        // A batch at a time, so that the quick client is never more than a batch behind, whatever the machine is doing.
        for (var sent = 1; sent < expected.Length; sent += 50)
        {
            var end = Math.Min(sent + 50, expected.Length);
            foreach (var message in expected[sent..end])
            {
                await feed.SendAsync(message);
            }

            await Wait.UntilAsync(() => _rig.Hubs.Messages("quick").Count == end);
        }

        _rig.Hubs.Messages("quick").ShouldBe(expected);
        quick.Aborted.ShouldBeFalse();
        app.Subscriptions[0].IsCancellationRequested.ShouldBeFalse();

        // The slow client's connection stays. When its sends go through, it gets the one that was under way, then hears
        // what it lost, as it asked for it, and nothing of what was queued.
        _rig.Hubs.Release("slow");
        await Wait.UntilAsync(() => _rig.Hubs.To("slow").Any(delivery => delivery.Method == "RealtimeError"));
        var lost = _rig.Hubs.To("slow").Single(delivery => delivery.Method == "RealtimeError").Lost;
        lost.Channel.ShouldBe(Order);
        lost.Topics.ShouldBe(["status", "tokens"]);
        lost.Reason.ShouldBe("behind");
        _rig.Hubs.Messages("slow").ShouldBe([expected[0]]);
        slow.Aborted.ShouldBeFalse();
        app.Bridge.ListenerCount.ShouldBe(2);

        // It joins again on the same connection and receives what comes next.
        await app.Bridge.SubscribeAsync(slow, Order, ["status", "tokens"], Cancel);
        await feed.SendAsync("""{"n":"next"}""");
        await Wait.UntilAsync(() => _rig.Hubs.Messages("slow").Count == 2);
        _rig.Hubs.Messages("slow")[1].ShouldBe("""{"n":"next"}""");
    }

    [Fact]
    public async Task The_number_of_messages_that_may_wait_for_a_connection_can_be_chosen()
    {
        var app = _rig.AddInstance(configure: options =>
        {
            options.MaxQueuedMessagesPerConnection = 8;
            options.MaxSubscriptionsPerConnection = 4;
        });
        var feed = new SseFeed();
        app.Api.Answer(feed);
        var slow = new FakeCaller("slow");
        var quick = new FakeCaller("quick");
        _rig.Hubs.Hold("slow");
        await app.Bridge.SubscribeAsync(slow, Order, ["status"], Cancel);
        await app.Bridge.SubscribeAsync(quick, Order, ["status"], Cancel);

        // One is under way and eight wait: the tenth is one too many.
        await feed.SendAsync("""{"n":0}""");
        await Wait.UntilAsync(() => _rig.Hubs.IsHeld("slow"));
        for (var n = 1; n < 10; n++)
        {
            await feed.SendAsync($$"""{"n":{{n}}}""");
            await Wait.UntilAsync(() => _rig.Hubs.Messages("quick").Count == n + 1);
        }

        _rig.Hubs.Release("slow");
        await Wait.UntilAsync(() => _rig.Hubs.To("slow").Any(delivery => delivery.Method == "RealtimeError"));
        _rig.Hubs.To("slow").Single(delivery => delivery.Method == "RealtimeError").Lost.Reason.ShouldBe("behind");
    }

    [Fact]
    public async Task A_client_that_catches_up_in_time_loses_nothing()
    {
        var app = _rig.AddInstance();
        var feed = new SseFeed();
        app.Api.Answer(feed);
        var slow = new FakeCaller("slow");
        _rig.Hubs.Hold("slow");
        await app.Bridge.SubscribeAsync(slow, Order, ["status"], Cancel);
        string[] expected = [.. Enumerable.Range(0, 400).Select(n => $$"""{"n":{{n}}}""")];

        foreach (var message in expected)
        {
            await feed.SendAsync(message);
        }

        // Its sends finish after all that, with fewer messages waiting than the queue holds.
        await Task.Delay(200, Cancel);
        _rig.Hubs.Release("slow");

        await Wait.UntilAsync(() => _rig.Hubs.Messages("slow").Count == expected.Length);
        _rig.Hubs.Messages("slow").ShouldBe(expected);
        slow.Aborted.ShouldBeFalse();
    }

    [Fact]
    public async Task A_client_whose_send_fails_loses_its_subscriptions_and_is_told_and_the_others_are_not()
    {
        var app = _rig.AddInstance();
        var feed = new SseFeed();
        app.Api.Answer(feed);
        var bad = new FakeCaller("bad");
        var good = new FakeCaller("good");
        _rig.Hubs.FailSendsTo("bad", new IOException("The connection is gone."), times: 1);
        await app.Bridge.SubscribeAsync(bad, Order, ["status"], Cancel);
        await app.Bridge.SubscribeAsync(good, Order, ["status"], Cancel);

        await feed.SendAsync("""{"n":1}""");
        await feed.SendAsync("""{"n":2}""");

        await Wait.UntilAsync(() => _rig.Hubs.To("bad").Any(delivery => delivery.Method == "RealtimeError"));
        var lost = _rig.Hubs.To("bad").Single(delivery => delivery.Method == "RealtimeError").Lost;
        lost.Channel.ShouldBe(Order);
        lost.Topics.ShouldBe(["status"]);
        lost.Reason.ShouldBe("send-failed");
        await Wait.UntilAsync(() => _rig.Hubs.Messages("good").Count == 2);
        _rig.Hubs.Messages("bad").ShouldNotContain("""{"n":1}""");
        bad.Aborted.ShouldBeFalse();
        good.Aborted.ShouldBeFalse();
        app.Bridge.ListenerCount.ShouldBe(2);
    }

    [Fact]
    public async Task A_client_that_cannot_be_told_of_its_loss_is_left_alone()
    {
        var app = _rig.AddInstance();
        var feed = new SseFeed();
        app.Api.Answer(feed);
        var bad = new FakeCaller("bad");
        _rig.Hubs.FailSendsTo("bad", new IOException("The connection is gone."));
        await app.Bridge.SubscribeAsync(bad, Order, ["status"], Cancel);

        await feed.SendAsync("""{"n":1}""");

        // The message and the news of its loss both fail, and that is the end of it: no loop, no abort.
        await Wait.UntilAsync(() => app.Subscriptions.Count == 1 && app.Subscriptions[0].IsCancellationRequested);
        _rig.Hubs.To("bad").ShouldBeEmpty();
        bad.Aborted.ShouldBeFalse();
        app.Bridge.FeedCount.ShouldBe(0);
    }

    [Fact]
    public async Task A_subscription_Inngest_refuses_is_reported_to_the_clients_who_can_then_join_again()
    {
        var app = _rig.AddInstance();
        app.Api.TokenFailure = new InngestException("Failed to get a subscription token for channel \"order:o-1\": 401 Unauthorized", 401);
        var caller = new FakeCaller("c1");

        await app.Bridge.SubscribeAsync(caller, Order, ["status"], Cancel);

        await Wait.UntilAsync(() => _rig.Hubs.To("c1").Any(delivery => delivery.Method == "RealtimeError"));
        var lost = _rig.Hubs.To("c1").Single(delivery => delivery.Method == "RealtimeError").Lost;
        lost.Channel.ShouldBe(Order);
        lost.Topics.ShouldBe(["status"]);
        lost.Reason.ShouldBe("inngest-ended");
        _rig.Hubs.Messages("c1").ShouldBeEmpty();
        app.Bridge.FeedCount.ShouldBe(0);
        app.Api.TokensMinted.ShouldBe(1);
        caller.Aborted.ShouldBeFalse();

        app.Api.TokenFailure = null;
        var feed = new SseFeed();
        app.Api.Answer(feed);
        await app.Bridge.SubscribeAsync(caller, Order, ["status"], Cancel);
        await feed.SendAsync("""{"n":1}""");
        await Wait.UntilAsync(() => _rig.Hubs.Messages("c1").Count == 1);
        app.Bridge.FeedCount.ShouldBe(1);
    }

    [Fact]
    public async Task A_lost_subscription_is_reported_as_the_client_asked_for_it_not_as_it_was_granted()
    {
        var app = _rig.AddInstance(request => ValueTask.FromResult<RealtimeSubscription?>(new($"user:{request.Caller.ConnectionId}", ["notifications"])));
        await app.Bridge.SubscribeAsync(new FakeCaller("c1"), "me", ["y", "x"], Cancel);

        app.Api.Fail(new InngestException("Failed to subscribe to realtime messages: 401 Unauthorized", 401));

        await Wait.UntilAsync(() => _rig.Hubs.To("c1").Any(delivery => delivery.Method == "RealtimeError"));
        var lost = _rig.Hubs.To("c1").Single(delivery => delivery.Method == "RealtimeError").Lost;
        lost.Channel.ShouldBe("me");
        lost.Topics.ShouldBe(["y", "x"]);
        lost.Reason.ShouldBe("inngest-ended");
    }

    [Fact]
    public async Task Each_way_a_client_asked_to_join_a_subscription_is_a_subscription_it_is_told_of()
    {
        var app = _rig.AddInstance(request => ValueTask.FromResult<RealtimeSubscription?>(new("user:1", ["n"])));
        var caller = new FakeCaller("c1");
        await app.Bridge.SubscribeAsync(caller, "me", ["y", "x"], Cancel);
        await app.Bridge.SubscribeAsync(caller, "mine", ["z"], Cancel);
        app.Bridge.FeedCount.ShouldBe(1);

        // Both are in one subscription to Inngest, which fails.
        app.Api.Fail(new InngestException("Failed to subscribe to realtime messages: 401 Unauthorized", 401));

        await Wait.UntilAsync(() => _rig.Hubs.To("c1").Count(delivery => delivery.Method == "RealtimeError") == 2);
        var lost = _rig.Hubs.To("c1").Where(delivery => delivery.Method == "RealtimeError").Select(delivery => delivery.Lost).OrderBy(item => item.Channel).ToArray();
        lost[0].Channel.ShouldBe("me");
        lost[0].Topics.ShouldBe(["y", "x"]);
        lost[1].Channel.ShouldBe("mine");
        lost[1].Topics.ShouldBe(["z"]);
        lost.ShouldAllBe(item => item.Reason == "inngest-ended");
        app.Bridge.FeedCount.ShouldBe(0);
    }

    [Fact]
    public async Task Subscriptions_that_overlap_deliver_a_message_they_share_once_each()
    {
        var app = _rig.AddInstance();
        var narrow = new SseFeed();
        var wide = new SseFeed();
        app.Api.Answer(narrow);
        app.Api.Answer(wide);
        var caller = new FakeCaller("c1");
        await app.Bridge.SubscribeAsync(caller, Order, ["status"], Cancel);
        await Wait.UntilAsync(() => app.Subscriptions.Count == 1);
        await app.Bridge.SubscribeAsync(caller, Order, ["status", "tokens"], Cancel);
        await Wait.UntilAsync(() => app.Subscriptions.Count == 2);

        // Inngest sends the status to each of the two subscriptions.
        await narrow.SendAsync("""{"topic":"status"}""");
        await wide.SendAsync("""{"topic":"status"}""");

        await Wait.UntilAsync(() => _rig.Hubs.Messages("c1").Count == 2);
        _rig.Hubs.Messages("c1").ShouldBe(["""{"topic":"status"}""", """{"topic":"status"}"""]);
        app.Bridge.FeedCount.ShouldBe(2);
    }

    [Fact]
    public async Task A_request_the_callback_now_grants_something_else_moves_to_it()
    {
        var channel = "order:a";
        var app = _rig.AddInstance(_ => ValueTask.FromResult<RealtimeSubscription?>(new(channel, ["status"])));
        app.Api.Answer(new SseFeed());
        app.Api.Answer(new SseFeed());
        var caller = new FakeCaller("c1");
        await app.Bridge.SubscribeAsync(caller, Order, ["status"], Cancel);
        await Wait.UntilAsync(() => app.Subscriptions.Count == 1);

        channel = "order:b";
        await app.Bridge.SubscribeAsync(caller, Order, ["status"], Cancel);

        await Wait.UntilAsync(() => app.Subscriptions.Count == 2);
        await Wait.UntilAsync(() => app.Subscriptions[0].IsCancellationRequested);
        app.Subscriptions[1].IsCancellationRequested.ShouldBeFalse();
        app.Bridge.FeedCount.ShouldBe(1);
        app.Bridge.Unsubscribe(caller, Order, ["status"]);
        await Wait.UntilAsync(() => app.Subscriptions[1].IsCancellationRequested);
        app.Bridge.FeedCount.ShouldBe(0);
    }

    [Fact]
    public async Task The_methods_clients_listen_on_can_be_chosen()
    {
        var app = _rig.AddInstance(configure: options =>
        {
            options.MessageMethod = "inngest";
            options.ErrorMethod = "inngestFailed";
        });
        var feed = new SseFeed();
        app.Api.Answer(feed);
        await app.Bridge.SubscribeAsync(new FakeCaller("c1"), Order, ["status"], Cancel);

        await feed.SendAsync("""{"n":1}""");

        await Wait.UntilAsync(() => _rig.Hubs.Messages("c1", "inngest").Count == 1);
        _rig.Hubs.Messages("c1").ShouldBeEmpty();
    }

    [Fact]
    public async Task When_the_app_stops_the_subscriptions_close_and_nobody_joins_any_more()
    {
        var app = _rig.AddInstance();
        app.Api.Answer(new SseFeed());
        var caller = new FakeCaller("c1");
        await app.Bridge.SubscribeAsync(caller, Order, ["status"], Cancel);
        await Wait.UntilAsync(() => app.Subscriptions.Count == 1);

        await app.Stopping.CancelAsync();

        await Wait.UntilAsync(() => app.Subscriptions[0].IsCancellationRequested);
        var exception = await Should.ThrowAsync<HubException>(() => app.Bridge.SubscribeAsync(new FakeCaller("c2"), Order, ["status"], Cancel).AsTask());
        exception.Message.ShouldBe("The server is shutting down.");
        caller.Aborted.ShouldBeFalse();
        app.Bridge.FeedCount.ShouldBe(0);
    }

    [Fact]
    public async Task Joining_leaving_and_disconnecting_while_messages_flow_loses_the_clients_who_stay_nothing()
    {
        var app = _rig.AddInstance();
        var feed = new SseFeed();
        app.Api.Answer(feed);
        var anchor = new FakeCaller("anchor");
        await app.Bridge.SubscribeAsync(anchor, Order, ["status"], Cancel);
        string[] expected = [.. Enumerable.Range(0, 1000).Select(n => $$"""{"n":{{n}}}""")];
        var callers = new ConcurrentBag<FakeCaller>();

        var sender = Task.Run(
            async () =>
            {
                for (var sent = 0; sent < expected.Length; sent += 100)
                {
                    foreach (var message in expected[sent..(sent + 100)])
                    {
                        await feed.SendAsync(message);
                    }

                    // The anchor stays within a batch of the feed, as only a client that falls 512 behind is disconnected.
                    await Wait.UntilAsync(() => _rig.Hubs.Messages("anchor").Count == sent + 100);
                }
            },
            Cancel);
        var churn = Enumerable.Range(0, 8).Select(worker => Task.Run(
            async () =>
            {
                for (var round = 0; round < 150; round++)
                {
                    var caller = new FakeCaller($"w{worker}-{round}");
                    callers.Add(caller);
                    await app.Bridge.SubscribeAsync(caller, Order, ["status"], Cancel);
                    switch (round % 3)
                    {
                        case 0:
                            app.Bridge.Unsubscribe(caller, Order, ["status"]);
                            break;
                        case 1:
                            caller.Disconnect();
                            break;
                    }
                }
            },
            Cancel));
        await Task.WhenAll(churn.Append(sender));

        await Wait.UntilAsync(() => _rig.Hubs.Messages("anchor").Count == expected.Length);
        _rig.Hubs.Messages("anchor").ShouldBe(expected);
        app.Api.TokensMinted.ShouldBe(1);

        // A connection that left its subscriptions stays known until it disconnects, so the rest do now.
        foreach (var caller in callers)
        {
            caller.Disconnect();
        }

        await Wait.UntilAsync(() => app.Bridge.ListenerCount == 1);
        anchor.Disconnect();
        await Wait.UntilAsync(() => app.Bridge.FeedCount == 0 && app.Bridge.ListenerCount == 0);
        await Wait.UntilAsync(() => app.Subscriptions[0].IsCancellationRequested);
    }

    [Fact]
    public async Task Subscriptions_that_open_and_close_all_the_time_leave_no_subscription_to_Inngest_open()
    {
        var app = _rig.AddInstance();
        string[][] topicSets = [["a"], ["a", "b"], ["b"]];
        var callers = new ConcurrentBag<FakeCaller>();

        var churn = Enumerable.Range(0, 8).Select(worker => Task.Run(
            async () =>
            {
                var random = new Random(worker);
                for (var round = 0; round < 100; round++)
                {
                    var caller = new FakeCaller($"w{worker}-{round}");
                    callers.Add(caller);
                    var topics = topicSets[random.Next(topicSets.Length)];
                    await app.Bridge.SubscribeAsync(caller, Order, topics, Cancel);
                    if (random.Next(2) == 0)
                    {
                        app.Bridge.Unsubscribe(caller, Order, topics);
                    }
                    else
                    {
                        caller.Disconnect();
                    }
                }
            },
            Cancel));
        await Task.WhenAll(churn);

        await Wait.UntilAsync(() => app.Bridge.FeedCount == 0);
        foreach (var caller in callers)
        {
            caller.Disconnect();
        }

        await Wait.UntilAsync(() => app.Bridge.ListenerCount == 0);
        await Wait.UntilAsync(() => app.Subscriptions.All(subscription => subscription.IsCancellationRequested));
    }
}
