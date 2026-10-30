// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using System.Buffers;
using System.IO.Pipelines;
using System.Text;
using System.Threading.Channels;
using Inngest.Realtime;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging;

namespace Inngest.AspNetCore;

// Relays realtime messages to browsers as server-sent events, one event per message with the JSON Inngest sends, so a
// page reads what Inngest's own subscribers read. Requests for the same topics of a channel share one subscription to
// Inngest, opened with the first request and closed with the last. Each request has a bounded queue: one that falls
// that far behind is ended, which an EventSource answers by reconnecting, instead of holding the others up.
internal sealed class RealtimeRelay(RealtimeSubscriber subscriber, TimeProvider time, ILogger logger, CancellationToken stopping)
{
    private const int QueueCapacity = 512;

    // Inngest's own interval, which proxies that close idle connections already tolerate.
    private static readonly TimeSpan KeepAliveInterval = TimeSpan.FromSeconds(15);

    private readonly Dictionary<string, Feed> _feeds = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    public async Task ServeAsync(HttpContext context, Func<HttpContext, ValueTask<RealtimeSubscription?>> resolve)
    {
        if (await resolve(context).ConfigureAwait(false) is not { } subscription)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        RealtimePayload.Check(subscription.Channel, subscription.Topics);
        var queue = Channel.CreateBounded<string>(new BoundedChannelOptions(QueueCapacity) { SingleReader = true, SingleWriter = true });
        var feed = Join(subscription, queue.Writer);
        using var ended = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, stopping);
        try
        {
            var response = context.Response;
            response.ContentType = "text/event-stream";
            response.Headers.CacheControl = "no-cache";

            // nginx would otherwise hold the events back until the response ends.
            response.Headers["X-Accel-Buffering"] = "no";
            context.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
            await RelayAsync(response.BodyWriter, queue.Reader, ended.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ended.IsCancellationRequested)
        {
            // The browser left, or the app is stopping.
        }
        finally
        {
            Leave(feed, queue.Writer);
        }
    }

    // Writes the queued messages as they come and a comment when nothing has been written for a while. The writes of
    // both take turns.
    private async Task RelayAsync(PipeWriter body, ChannelReader<string> queue, CancellationToken cancellationToken)
    {
        using var turn = new SemaphoreSlim(1, 1);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var lastWrite = time.GetTimestamp();
        Comment(body);
        await body.FlushAsync(cancellationToken).ConfigureAwait(false);
        var keepAlive = KeepAliveAsync();
        try
        {
            await foreach (var message in queue.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                await turn.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    Event(body, message);
                    while (queue.TryRead(out var next))
                    {
                        Event(body, next);
                    }

                    await body.FlushAsync(cancellationToken).ConfigureAwait(false);
                    lastWrite = time.GetTimestamp();
                }
                finally
                {
                    turn.Release();
                }
            }
        }
        catch (ChannelClosedException)
        {
            // Inngest refused the subscription, or the browser fell too far behind: the response ends.
        }
        finally
        {
            await stop.CancelAsync().ConfigureAwait(false);
            await keepAlive.ConfigureAwait(false);
        }

        async Task KeepAliveAsync()
        {
            using var timer = new PeriodicTimer(KeepAliveInterval, time);
            try
            {
                while (await timer.WaitForNextTickAsync(stop.Token).ConfigureAwait(false))
                {
                    await turn.WaitAsync(stop.Token).ConfigureAwait(false);
                    try
                    {
                        if (time.GetElapsedTime(lastWrite) >= KeepAliveInterval)
                        {
                            Comment(body);
                            await body.FlushAsync(stop.Token).ConfigureAwait(false);
                            lastWrite = time.GetTimestamp();
                        }
                    }
                    finally
                    {
                        turn.Release();
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    private Feed Join(RealtimeSubscription subscription, ChannelWriter<string> queue)
    {
        var topics = RealtimePayload.DistinctTopics(subscription.Topics);
        var key = RealtimePayload.SubscriptionKey(subscription.Channel, topics);
        Feed feed;
        lock (_gate)
        {
            if (_feeds.TryGetValue(key, out var current))
            {
                current.Queues = [.. current.Queues, queue];
                return current;
            }

            feed = new Feed(key) { Queues = [queue] };
            _feeds.Add(key, feed);
        }

        _ = Task.Run(() => PumpAsync(feed, subscription.Channel, topics));
        return feed;
    }

    private void Leave(Feed feed, ChannelWriter<string> queue)
    {
        queue.TryComplete();
        lock (_gate)
        {
            feed.Queues = Array.FindAll(feed.Queues, other => other != queue);
            if (feed.Queues.Length == 0 && Forget(feed))
            {
                feed.Stop.Cancel();
            }
        }
    }

    // Removes the feed unless a newer one for the same topics has taken its place. Called under the lock.
    private bool Forget(Feed feed) => _feeds.TryGetValue(feed.Key, out var current) && current == feed && _feeds.Remove(feed.Key);

    // Hands every message of the subscription to the queues of the feed's requests.
    private async Task PumpAsync(Feed feed, string channel, IReadOnlyList<string> topics)
    {
        try
        {
            await foreach (var message in subscriber.ReadAsync(channel, topics, feed.Stop.Token).ConfigureAwait(false))
            {
                foreach (var queue in Volatile.Read(ref feed.Queues))
                {
                    if (!queue.TryWrite(message))
                    {
                        queue.TryComplete(new ChannelClosedException("The browser fell too far behind."));
                    }
                }
            }
        }
        catch (OperationCanceledException) when (feed.Stop.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            Log.RealtimeRelayFailed(logger, channel, exception.Message);
            lock (_gate)
            {
                Forget(feed);
                foreach (var queue in feed.Queues)
                {
                    queue.TryComplete(new ChannelClosedException("Inngest refused the subscription.", exception));
                }
            }
        }
    }

    private static void Event(PipeWriter body, string message)
    {
        var span = body.GetSpan(8 + Encoding.UTF8.GetMaxByteCount(message.Length));
        "data: "u8.CopyTo(span);
        var length = 6 + Encoding.UTF8.GetBytes(message, span[6..]);
        span[length++] = (byte)'\n';
        span[length++] = (byte)'\n';
        body.Advance(length);
    }

    private static void Comment(PipeWriter body) => body.Write(":\n\n"u8);

    private sealed class Feed(string key)
    {
        public string Key { get; } = key;

        // Not disposed: it has no timer, and a request may still cancel it while the pump ends.
        public CancellationTokenSource Stop { get; } = new();

        // Replaced under the lock, never changed, so the pump reads the current queues without it.
        public ChannelWriter<string>[] Queues = [];
    }
}
