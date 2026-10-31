// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.

using System.Diagnostics;
using System.Globalization;
using Inngest.Realtime;
using Inngest.SignalR;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Log = Inngest.SignalR.Log;

namespace Inngest;

/// <summary>
/// Forwards Inngest realtime messages to the clients of the SignalR hub THub. A hub method of the app calls
/// <see cref="SubscribeAsync(HubCallerContext, string, IReadOnlyList{string}, CancellationToken)"/> to join the
/// calling client to the topics of a channel, after <see cref="InngestSignalROptions.Authorize"/> has agreed. Clients
/// that join the same topics share one subscription to Inngest, which opens with the first and closes with the last
/// that leaves or disconnects. A message goes to each client as the JSON Inngest sends it, in a string, to the client
/// method <see cref="InngestSignalROptions.MessageMethod"/>.
/// </summary>
/// <remarks>
/// Every connection has a queue and a loop of its own. A client that reads too slowly lags only itself, and one that
/// falls too far behind loses its subscriptions: what is queued for it is dropped, and it is told through
/// <see cref="InngestSignalROptions.ErrorMethod"/> what it lost, to join again. Its connection stays open. Messages are
/// sent to the connections that joined through this instance, by connection id and never to a group, so an app with
/// several instances on a backplane such as Redis or Azure SignalR Service delivers each message once: every instance
/// subscribes to Inngest for its own clients. A client that reconnects has a new connection id and joins again.
/// <c>AddInngestSignalR</c> registers the type, and <see cref="InngestRealtimeHub"/> is a hub that calls it.
/// </remarks>
/// <typeparam name="THub">The hub whose clients receive the messages.</typeparam>
public sealed class InngestRealtimeBridge<THub> : IDisposable
    where THub : Hub
{
    // Why a client lost a subscription, as ErrorMethod tells it.
    private const string BehindReason = "behind";
    private const string SendFailedReason = "send-failed";
    private const string InngestEndedReason = "inngest-ended";

    private readonly RealtimeSubscriber _subscriber;
    private readonly IHubContext<THub> _hub;
    private readonly IServiceScopeFactory _scopes;
    private readonly InngestSignalROptions _options;
    private readonly ILogger _logger;
    private readonly Dictionary<string, Feed> _feeds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Listener> _listeners = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private readonly CancellationTokenRegistration _stopping;
    private bool _stopped;

    internal InngestRealtimeBridge(
        RealtimeSubscriber subscriber,
        IHubContext<THub> hub,
        IServiceScopeFactory scopes,
        InngestSignalROptions options,
        ILogger logger,
        CancellationToken stopping)
    {
        ArgumentNullException.ThrowIfNull(options.Authorize);
        _subscriber = subscriber;
        _hub = hub;
        _scopes = scopes;
        _options = options;
        _logger = logger;
        _stopping = stopping.Register(static state => ((InngestRealtimeBridge<THub>)state!).StopAll(), this);
    }

    // What the tests look at: nothing is left behind once every client has left.
    internal int FeedCount
    {
        get
        {
            lock (_gate)
            {
                return _feeds.Count;
            }
        }
    }

    internal int ListenerCount
    {
        get
        {
            lock (_gate)
            {
                return _listeners.Count;
            }
        }
    }

    /// <summary>
    /// Joins the calling client to these topics of the channel if <see cref="InngestSignalROptions.Authorize"/> lets it
    /// in, and returns the subscription it joined, which is what the callback returned. Joining what the client is in
    /// already changes nothing. The subscription to Inngest opens with the first client that joins it, and not before
    /// this returns: messages published before it is open are not received, as Inngest keeps none, and nothing tells
    /// the client when it is open.
    /// </summary>
    /// <param name="caller">The caller of the hub method, such as the hub's <c>Context</c>.</param>
    /// <param name="channel">The channel the client asks for.</param>
    /// <param name="topics">The topics of the channel the client asks for; at least one.</param>
    /// <param name="cancellationToken">Passed to the callback; the caller's disconnect by default.</param>
    /// <exception cref="HubException">
    /// The request is incomplete, the callback refuses it, the client is in as many subscriptions as
    /// <see cref="InngestSignalROptions.MaxSubscriptionsPerConnection"/> allows, or the app is shutting down. SignalR
    /// sends the message of a HubException to the client.
    /// </exception>
    public async ValueTask<RealtimeSubscription> SubscribeAsync(
        HubCallerContext caller,
        string channel,
        IReadOnlyList<string> topics,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(caller);
        if (string.IsNullOrEmpty(channel))
        {
            throw new HubException("A channel is required.");
        }

        if (topics is null || topics.Count == 0 || topics.Any(string.IsNullOrEmpty))
        {
            throw new HubException("At least one topic is required, and none may be empty.");
        }

        var requested = new RealtimeSubscription(channel, topics);
        var granted = await AuthorizeAsync(caller, requested, cancellationToken).ConfigureAwait(false);
        Join(caller, requested, granted);
        return granted;
    }

    /// <summary>
    /// Joins the calling client to a typed topic, as <see cref="SubscribeAsync(HubCallerContext, string, IReadOnlyList{string}, CancellationToken)"/>
    /// does for its channel and name, so that a hub names the topics a function publishes to once.
    /// </summary>
    public ValueTask<RealtimeSubscription> SubscribeAsync<T>(HubCallerContext caller, RealtimeTopic<T> topic, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(topic);
        return SubscribeAsync(caller, topic.Channel, [topic.Name], cancellationToken);
    }

    /// <summary>
    /// Takes the calling client out of the subscription it joined with these arguments. They are those of
    /// <see cref="SubscribeAsync(HubCallerContext, string, IReadOnlyList{string}, CancellationToken)"/> or the
    /// subscription that returned, which differ when the callback changed the request. Leaving what the client is not
    /// in changes nothing. The subscription to Inngest closes with the last client that leaves.
    /// </summary>
    public void Unsubscribe(HubCallerContext caller, string channel, IReadOnlyList<string> topics)
    {
        ArgumentNullException.ThrowIfNull(caller);
        if (string.IsNullOrEmpty(channel) || topics is null)
        {
            return;
        }

        var key = RealtimePayload.SubscriptionKey(channel, RealtimePayload.DistinctTopics(topics));
        List<Feed>? orphaned = null;
        lock (_gate)
        {
            if (!_listeners.TryGetValue(caller.ConnectionId, out var listener))
            {
                return;
            }

            // What was asked for under this key, and what was granted under it.
            List<string>? leaving = null;
            foreach (var (requestKey, request) in listener.Requests)
            {
                if (requestKey == key || request.Feed.Key == key)
                {
                    (leaving ??= []).Add(requestKey);
                }
            }

            if (leaving is null)
            {
                return;
            }

            foreach (var requestKey in leaving)
            {
                var feed = listener.Requests[requestKey].Feed;
                listener.Requests.Remove(requestKey);
                LeaveIfUnused(listener, feed, ref orphaned);
            }
        }

        Close(orphaned);
    }

    /// <summary>Takes the calling client out of a typed topic's subscription, as <see cref="Unsubscribe(HubCallerContext, string, IReadOnlyList{string})"/> does.</summary>
    public void Unsubscribe<T>(HubCallerContext caller, RealtimeTopic<T> topic)
    {
        ArgumentNullException.ThrowIfNull(topic);
        Unsubscribe(caller, topic.Channel, [topic.Name]);
    }

    void IDisposable.Dispose()
    {
        _stopping.Dispose();
        StopAll();
    }

    // The loops outlive the hub call that starts them, so they do not belong to its activity.
    private static void RunDetached(Func<Task> work) => _ = Task.Run(() =>
    {
        Activity.Current = null;
        return work();
    });

    private static string KeyOf(RealtimeSubscription subscription) =>
        RealtimePayload.SubscriptionKey(subscription.Channel, RealtimePayload.DistinctTopics(subscription.Topics));

    private async ValueTask<RealtimeSubscription> AuthorizeAsync(HubCallerContext caller, RealtimeSubscription requested, CancellationToken cancellationToken)
    {
        RealtimeSubscription? granted;
        var scope = _scopes.CreateAsyncScope();
        await using (scope.ConfigureAwait(false))
        {
            var request = new RealtimeSubscribeRequest(
                caller,
                requested,
                scope.ServiceProvider,
                cancellationToken.CanBeCanceled ? cancellationToken : caller.ConnectionAborted);
            granted = await _options.Authorize!(request).ConfigureAwait(false);
        }

        if (granted is null || granted.Topics.Count == 0)
        {
            throw new HubException("You may not subscribe to this channel.");
        }

        RealtimePayload.Check(granted.Channel, granted.Topics);
        return granted;
    }

    private void Join(HubCallerContext caller, RealtimeSubscription requested, RealtimeSubscription granted)
    {
        var topics = RealtimePayload.DistinctTopics(granted.Topics);
        var key = RealtimePayload.SubscriptionKey(granted.Channel, topics);
        var requestKey = KeyOf(requested);
        List<Feed>? orphaned = null;
        Listener listener;
        Feed feed;
        var newListener = false;
        var newFeed = false;
        lock (_gate)
        {
            if (_stopped)
            {
                throw new HubException("The server is shutting down.");
            }

            if (_listeners.TryGetValue(caller.ConnectionId, out var known))
            {
                listener = known;
            }
            else
            {
                listener = new Listener(caller.ConnectionId, _options.MaxQueuedMessagesPerConnection);
                _listeners.Add(caller.ConnectionId, listener);
                newListener = true;
            }

            if (listener.Requests.TryGetValue(requestKey, out var current))
            {
                if (current.Feed.Key == key)
                {
                    // In it already.
                    return;
                }

                // The callback grants something else than it did, so the request moves to that.
                listener.Requests.Remove(requestKey);
                LeaveIfUnused(listener, current.Feed, ref orphaned);
            }
            else if (listener.Requests.Count >= _options.MaxSubscriptionsPerConnection)
            {
                throw new HubException(string.Create(CultureInfo.InvariantCulture, $"A connection may be joined to at most {_options.MaxSubscriptionsPerConnection} subscriptions."));
            }

            if (_feeds.TryGetValue(key, out var shared))
            {
                feed = shared;
            }
            else
            {
                feed = new Feed(key, granted.Channel, topics);
                _feeds.Add(key, feed);
                newFeed = true;
            }

            if (Array.IndexOf(feed.Listeners, listener) < 0)
            {
                Volatile.Write(ref feed.Listeners, [.. feed.Listeners, listener]);
            }

            listener.Requests[requestKey] = new Request(feed, requested.Channel, [.. requested.Topics]);
        }

        Close(orphaned);
        if (newListener)
        {
            // However the connection ends, it leaves its feeds. A token that is cancelled already calls back at once.
            Action ended = () => End(listener);
            caller.ConnectionAborted.UnsafeRegister(static state => ((Action)state!)(), ended);
            RunDetached(() => SendLoopAsync(listener));
        }

        if (newFeed)
        {
            RunDetached(() => PumpAsync(feed));
        }
    }

    // Takes the listener out of the feed unless another of its requests is in it. Called under the lock.
    private void LeaveIfUnused(Listener listener, Feed feed, ref List<Feed>? orphaned)
    {
        foreach (var request in listener.Requests.Values)
        {
            if (request.Feed == feed)
            {
                return;
            }
        }

        RemoveListener(feed, listener, ref orphaned);
    }

    // Takes the listener out of the feed. One that nobody listens to any more is forgotten and, once the lock is
    // released, closed. Called under the lock.
    private void RemoveListener(Feed feed, Listener listener, ref List<Feed>? orphaned)
    {
        var current = feed.Listeners;
        var index = Array.IndexOf(current, listener);
        if (index < 0)
        {
            return;
        }

        var rest = new Listener[current.Length - 1];
        Array.Copy(current, 0, rest, 0, index);
        Array.Copy(current, index + 1, rest, index, rest.Length - index);
        Volatile.Write(ref feed.Listeners, rest);
        if (rest.Length == 0 && Forget(feed))
        {
            (orphaned ??= []).Add(feed);
        }
    }

    // Removes the feed unless a newer one for the same topics has taken its place. Called under the lock.
    private bool Forget(Feed feed) => _feeds.TryGetValue(feed.Key, out var current) && current == feed && _feeds.Remove(feed.Key);

    private void Close(List<Feed>? feeds)
    {
        if (feeds is null)
        {
            return;
        }

        foreach (var feed in feeds)
        {
            Log.FeedClosed(_logger, feed.Channel);
            feed.Stop.Cancel();
        }
    }

    // The connection has gone. Returns whether this call ended it.
    private bool End(Listener listener)
    {
        List<Feed>? orphaned = null;
        lock (_gate)
        {
            if (listener.Ended)
            {
                return false;
            }

            listener.Ended = true;
            if (_listeners.TryGetValue(listener.ConnectionId, out var current) && current == listener)
            {
                _listeners.Remove(listener.ConnectionId);
            }

            foreach (var request in listener.Requests.Values)
            {
                RemoveListener(request.Feed, listener, ref orphaned);
            }

            listener.Requests.Clear();
        }

        Close(orphaned);
        listener.Queue.Writer.TryComplete();
        listener.Stop.Cancel();
        return true;
    }

    // The connection cannot keep up, or cannot be sent to. A message it misses is a gap it cannot know of, so it starts
    // over: it is taken out of all its subscriptions, what is queued for it but news is dropped, and it is told which
    // subscriptions it lost, as it asked for them, to join them again. The connection stays open, for the app may use
    // it for more than this, and ending it would tell the client not to reconnect. Returns whether it lost any.
    private bool Lose(Listener listener, string reason)
    {
        List<Feed>? orphaned = null;
        lock (_gate)
        {
            if (listener.Ended || listener.Requests.Count == 0)
            {
                return false;
            }

            var lost = TakeAll(listener, ref orphaned);
            DropMessages(listener);
            Notify(listener, lost, reason);
        }

        Close(orphaned);
        return true;
    }

    // Queues the news that these subscriptions of the listener are lost. The news is never dropped for want of room: if
    // the queue has none, the connection is too far behind to be told one thing at a time, and it starts over as Lose
    // has it do, with the subscriptions it has left lost as well and told as "behind". Returns whether it came to that.
    // Called under the lock.
    private bool Tell(Listener listener, List<Request> lost, string reason, ref List<Feed>? orphaned)
    {
        var told = 0;
        while (told < lost.Count && listener.Queue.Writer.TryWrite(new Outgoing(reason, lost[told].Channel, lost[told].Topics)))
        {
            told++;
        }

        if (told == lost.Count)
        {
            return false;
        }

        var behind = TakeAll(listener, ref orphaned);
        DropMessages(listener);
        Notify(listener, lost.GetRange(told, lost.Count - told), reason);
        Notify(listener, behind, BehindReason);
        return true;
    }

    // Takes the listener out of all the feeds it is in, and returns what it asked for to be in them. Called under the lock.
    private List<Request> TakeAll(Listener listener, ref List<Feed>? orphaned)
    {
        List<Request> taken = [.. listener.Requests.Values];
        listener.Requests.Clear();
        foreach (var request in taken)
        {
            RemoveListener(request.Feed, listener, ref orphaned);
        }

        return taken;
    }

    // Empties the queue of its messages, which would arrive after a gap, and leaves the news in it. Called under the lock.
    private static void DropMessages(Listener listener)
    {
        List<Outgoing>? news = null;
        while (listener.Queue.Reader.TryRead(out var queued))
        {
            if (queued.Channel is not null)
            {
                (news ??= []).Add(queued);
            }
        }

        foreach (var item in news ?? [])
        {
            listener.Queue.Writer.TryWrite(item);
        }
    }

    // Called under the lock, after DropMessages, when the queue has room unless a client has not read news for as long as
    // it takes to lose more subscriptions than the queue holds.
    private static void Notify(Listener listener, List<Request> lost, string reason)
    {
        foreach (var request in lost)
        {
            listener.Queue.Writer.TryWrite(new Outgoing(reason, request.Channel, request.Topics));
        }
    }

    // Hands every message of the subscription to the queues of the feed's connections.
    private async Task PumpAsync(Feed feed)
    {
        Log.FeedOpened(_logger, feed.Channel);
        try
        {
            await foreach (var message in _subscriber.ReadAsync(feed.Channel, feed.Topics, feed.Stop.Token).ConfigureAwait(false))
            {
                foreach (var listener in Volatile.Read(ref feed.Listeners))
                {
                    Offer(listener, new Outgoing(message));
                }
            }
        }
        catch (Exception exception) when (!feed.Stop.IsCancellationRequested)
        {
            Fail(feed, exception);
        }
        catch (Exception)
        {
            // The feed was closed, and what ends the read is the cancellation, whatever form it takes.
        }
    }

    // Inngest refused the subscription, or it cannot be kept: the feed's connections are out of it and are told, so that
    // joining again tries anew.
    private void Fail(Feed feed, Exception exception)
    {
        Log.FeedFailed(_logger, feed.Channel, exception.Message);
        List<Feed>? orphaned = null;
        List<string>? behind = null;
        lock (_gate)
        {
            Forget(feed);
            var affected = feed.Listeners;
            Volatile.Write(ref feed.Listeners, []);
            foreach (var listener in affected)
            {
                List<Request>? lost = null;
                foreach (var (requestKey, request) in listener.Requests.ToArray())
                {
                    if (request.Feed == feed)
                    {
                        listener.Requests.Remove(requestKey);
                        (lost ??= []).Add(request);
                    }
                }

                if (lost is not null && Tell(listener, lost, InngestEndedReason, ref orphaned))
                {
                    (behind ??= []).Add(listener.ConnectionId);
                }
            }
        }

        Close(orphaned);
        foreach (var connectionId in behind ?? [])
        {
            Log.ListenerFellBehind(_logger, connectionId, _options.MaxQueuedMessagesPerConnection);
        }
    }

    // Never waits: a connection whose queue is full is too far behind and starts over, so that it does not hold up the
    // pump that feeds all the others.
    private void Offer(Listener listener, Outgoing item)
    {
        if (listener.Queue.Writer.TryWrite(item) || listener.Ended)
        {
            return;
        }

        if (Lose(listener, BehindReason))
        {
            Log.ListenerFellBehind(_logger, listener.ConnectionId, _options.MaxQueuedMessagesPerConnection);
        }
    }

    // Sends the queue to the connection, one message at a time and in order. Only this connection waits for a slow client.
    private async Task SendLoopAsync(Listener listener)
    {
        var client = _hub.Clients.Client(listener.ConnectionId);
        var stop = listener.Stop.Token;
        try
        {
            await foreach (var item in listener.Queue.Reader.ReadAllAsync(stop).ConfigureAwait(false))
            {
                try
                {
                    await (item.Channel is null
                        ? client.SendAsync(_options.MessageMethod, item.Text, stop)
                        : client.SendAsync(_options.ErrorMethod, item.Channel, item.Topics, item.Text, stop)).ConfigureAwait(false);
                }
                catch (Exception exception) when (!stop.IsCancellationRequested)
                {
                    // The news of a loss can fail in the same way, and then there is nothing more to do for the client.
                    if (item.Channel is null && Lose(listener, SendFailedReason))
                    {
                        Log.ListenerSendFailed(_logger, listener.ConnectionId, exception.Message);
                    }
                }
            }
        }
        catch (Exception) when (stop.IsCancellationRequested)
        {
            // The connection ended, and what ends the loop is the cancellation, whatever form it takes.
        }
    }

    // The app is stopping: the subscriptions to Inngest close now, rather than when the connections have drained.
    private void StopAll()
    {
        Feed[] feeds;
        Listener[] listeners;
        lock (_gate)
        {
            _stopped = true;
            feeds = [.. _feeds.Values];
            listeners = [.. _listeners.Values];
            _feeds.Clear();
            _listeners.Clear();
            foreach (var listener in listeners)
            {
                listener.Ended = true;
            }
        }

        foreach (var feed in feeds)
        {
            feed.Stop.Cancel();
        }

        foreach (var listener in listeners)
        {
            listener.Queue.Writer.TryComplete();
            listener.Stop.Cancel();
        }
    }
}
