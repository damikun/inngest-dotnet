// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using System.Diagnostics;
using System.Globalization;
using System.Text;
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
/// falls too far behind is disconnected. Messages are sent to the connections that joined through this instance, by
/// connection id and never to a group, so an app with several instances on a backplane such as Redis or Azure SignalR
/// Service delivers each message once: every instance subscribes to Inngest for its own clients. A client that
/// reconnects has a new connection id and joins again. <c>AddInngestSignalR</c> registers the type, and
/// <see cref="InngestRealtimeHub"/> is a hub that calls it.
/// </remarks>
/// <typeparam name="THub">The hub whose clients receive the messages.</typeparam>
public sealed class InngestRealtimeBridge<THub> : IDisposable
    where THub : Hub
{
    // Inngest keeps nothing for a client that is this far behind, so it is better to end its connection, which its
    // client answers by reconnecting and joining again, than to hold the messages back for long.
    private const int QueueCapacity = 512;

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
    /// already changes nothing. The subscription to Inngest opens with the first client that joins it; messages
    /// published before it is open are not received, as Inngest keeps none.
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

        var key = KeyOf(channel, Normalize(topics));
        List<Feed>? orphaned = null;
        lock (_gate)
        {
            if (!_listeners.TryGetValue(caller.ConnectionId, out var listener) || !listener.Feeds.TryGetValue(key, out var feed))
            {
                return;
            }

            Unindex(listener, feed);
            RemoveListener(feed, listener, ref orphaned);
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

    // Distinct and sorted, so that the same topics in any order are one subscription.
    private static string[] Normalize(IEnumerable<string> topics) => [.. topics.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];

    // Lengths come first, so that names which contain a separator cannot run together into the key of another
    // subscription and let a client in on it.
    private static string KeyOf(string channel, string[] topics)
    {
        var key = new StringBuilder();
        key.Append(channel.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(channel);
        foreach (var topic in topics)
        {
            key.Append(';').Append(topic.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(topic);
        }

        return key.ToString();
    }

    // The loops outlive the hub call that starts them, so they do not belong to its activity.
    private static void RunDetached(Func<Task> work) => _ = Task.Run(() =>
    {
        Activity.Current = null;
        return work();
    });

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
        var topics = Normalize(granted.Topics);
        var key = KeyOf(granted.Channel, topics);
        var requestedKey = KeyOf(requested.Channel, Normalize(requested.Topics));
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
                listener = new Listener(caller.ConnectionId, caller.Abort, QueueCapacity);
                _listeners.Add(caller.ConnectionId, listener);
                newListener = true;
            }

            if (listener.Feeds.TryGetValue(key, out var joined))
            {
                // In it already: only the way the client asked for it can be new.
                listener.Feeds.TryAdd(requestedKey, joined);
                return;
            }

            if (listener.FeedCount >= _options.MaxSubscriptionsPerConnection)
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

            Volatile.Write(ref feed.Listeners, [.. feed.Listeners, listener]);
            listener.Feeds[key] = feed;
            listener.Feeds.TryAdd(requestedKey, feed);
            listener.FeedCount++;
        }

        if (newListener)
        {
            // However the connection ends, it leaves its feeds. A token that is cancelled already calls back at once.
            Action ended = () => End(listener);
            caller.ConnectionAborted.UnsafeRegister(static state => ((Action)state!)(), ended);
            RunDetached(() => SendAsync(listener));
        }

        if (newFeed)
        {
            RunDetached(() => PumpAsync(feed));
        }
    }

    // Forgets the feed in the listener's names for it. Called under the lock.
    private static void Unindex(Listener listener, Feed feed)
    {
        List<string>? names = null;
        foreach (var (name, joined) in listener.Feeds)
        {
            if (joined == feed)
            {
                (names ??= []).Add(name);
            }
        }

        if (names is null)
        {
            return;
        }

        foreach (var name in names)
        {
            listener.Feeds.Remove(name);
        }

        listener.FeedCount--;
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

    // The connection has gone, or must go. Returns whether this call ended it.
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

            foreach (var feed in listener.Feeds.Values)
            {
                RemoveListener(feed, listener, ref orphaned);
            }

            listener.Feeds.Clear();
            listener.FeedCount = 0;
        }

        Close(orphaned);
        listener.Queue.Writer.TryComplete();
        listener.Stop.Cancel();
        return true;
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
                    Offer(listener, new Outgoing(null, message));
                }
            }
        }
        catch (OperationCanceledException) when (feed.Stop.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            Fail(feed, exception);
        }
    }

    // Inngest refused the subscription, or it cannot be kept: the feed's connections are out of it and are told, so that
    // joining again tries anew.
    private void Fail(Feed feed, Exception exception)
    {
        Log.FeedFailed(_logger, feed.Channel, exception.Message);
        Listener[] affected;
        lock (_gate)
        {
            Forget(feed);
            affected = feed.Listeners;
            Volatile.Write(ref feed.Listeners, []);
            foreach (var listener in affected)
            {
                Unindex(listener, feed);
            }
        }

        foreach (var listener in affected)
        {
            Offer(listener, new Outgoing(feed.Channel, "The subscription to Inngest ended."));
        }
    }

    // Never waits: a connection whose queue is full is too far behind and is disconnected, so that it does not hold up
    // the pump that feeds all the others.
    private void Offer(Listener listener, Outgoing item)
    {
        if (listener.Queue.Writer.TryWrite(item) || listener.Ended)
        {
            return;
        }

        if (End(listener))
        {
            Log.ListenerFellBehind(_logger, listener.ConnectionId, QueueCapacity);
            listener.Abort();
        }
    }

    // Sends the queue to the connection, one message at a time and in order. Only this connection waits for a slow client.
    private async Task SendAsync(Listener listener)
    {
        var client = _hub.Clients.Client(listener.ConnectionId);
        var stop = listener.Stop.Token;
        try
        {
            await foreach (var item in listener.Queue.Reader.ReadAllAsync(stop).ConfigureAwait(false))
            {
                await (item.Channel is null
                    ? client.SendAsync(_options.MessageMethod, item.Text, stop)
                    : client.SendAsync(_options.ErrorMethod, item.Channel, item.Text, stop)).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            // A message that did not go out is a gap the client cannot know of, so it had better reconnect.
            if (End(listener))
            {
                Log.ListenerSendFailed(_logger, listener.ConnectionId, exception.Message);
                listener.Abort();
            }
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
