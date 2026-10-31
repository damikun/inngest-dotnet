// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using System.Net;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;

namespace Inngest.Realtime;

// Yields the JSON of every message of a subscription, as Inngest sends it. A connection that ends is opened again with
// a new token: Inngest closes one after 15 minutes, and a token is only good for connecting within a minute. Messages
// published while it reconnects are lost, as Inngest keeps none. A connection silent for three of Inngest's 15-second
// keepalives is taken for dead. A refusal, such as a token Inngest does not accept, ends the subscription with it.
internal sealed class RealtimeSubscriber(IRealtimeApi api, TimeProvider time, ILogger<RealtimeSubscriber> logger)
{
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan FirstRetryDelay = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromSeconds(30);

    // A connection that ends sooner than this counts as a failure, so a server that keeps closing them is not hammered.
    private static readonly TimeSpan MinHealthyConnection = TimeSpan.FromSeconds(5);

    public IAsyncEnumerable<string> ReadAsync(string channel, IReadOnlyList<string> topics, CancellationToken cancellationToken) =>
        ReadAsync(channel, topics, null, cancellationToken);

    // Tells onConnection when it connects, has connected and loses the connection, from the thread that reads.
    public async IAsyncEnumerable<string> ReadAsync(
        string channel,
        IReadOnlyList<string> topics,
        Action<RealtimeConnection>? onConnection,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var failures = 0;
        var opened = false;
        onConnection?.Invoke(RealtimeConnection.Connecting);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Exception? failure = null;
            var connectedAt = time.GetTimestamp();
            HttpResponseMessage? response = null;
            try
            {
                var token = await api.GetTokenAsync(channel, topics, cancellationToken).ConfigureAwait(false);
                response = await api.OpenSubscriptionAsync(token, cancellationToken).ConfigureAwait(false);
            }
            catch (InngestException exception) when (IsTransient(exception))
            {
                failure = exception;
            }

            if (response is not null)
            {
                using (response)
                {
                    Log.RealtimeConnected(logger, channel);
                    opened = true;
                    onConnection?.Invoke(RealtimeConnection.Open);
                    var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                    var events = ServerSentEvents.ReadDataAsync(stream, time, IdleTimeout, cancellationToken).GetAsyncEnumerator(cancellationToken);
                    await using (events.ConfigureAwait(false))
                    {
                        while (true)
                        {
                            try
                            {
                                if (!await events.MoveNextAsync().ConfigureAwait(false))
                                {
                                    break;
                                }
                            }
                            catch (Exception exception) when (exception is IOException or HttpRequestException or TimeoutException && !cancellationToken.IsCancellationRequested)
                            {
                                failure = exception;
                                break;
                            }

                            yield return events.Current;
                        }
                    }
                }

                // A connection that held for a while was working, so whatever ended it starts the delays over. One that did
                // not counts with the failures before it, whatever it delivered, or a server that sends a message and closes
                // would be met with the first delay for ever.
                if (time.GetElapsedTime(connectedAt) >= MinHealthyConnection)
                {
                    failures = 0;
                    if (failure is null)
                    {
                        onConnection?.Invoke(RealtimeConnection.Reconnecting);
                        continue;
                    }
                }

                failure ??= new IOException("Inngest closed the subscription right after opening it.");
            }

            failures++;
            var delay = RetryDelay(failures);
            Log.RealtimeReconnecting(logger, channel, failure!.Message, (long)delay.TotalMilliseconds);
            onConnection?.Invoke(opened ? RealtimeConnection.Reconnecting : RealtimeConnection.Connecting);
            await Task.Delay(delay, time, cancellationToken).ConfigureAwait(false);
        }
    }

    // Rate limits and failures of Inngest pass; a refusal of the request does not.
    private static bool IsTransient(InngestException exception) =>
        exception.StatusCode is null or (int)HttpStatusCode.TooManyRequests or (int)HttpStatusCode.RequestTimeout or >= 500;

    private static TimeSpan RetryDelay(int failures)
    {
        var delay = FirstRetryDelay * Math.Pow(2, Math.Min(failures - 1, 10));
        delay = delay > MaxRetryDelay ? MaxRetryDelay : delay;

        // Up to a fifth less, so that subscribers that failed together do not come back together.
        return delay * (1 - (Random.Shared.NextDouble() * 0.2));
    }
}

// What the connection of a subscription is doing, for the UI packages that show it.
internal enum RealtimeConnection
{
    Connecting,
    Open,
    Reconnecting,
}
