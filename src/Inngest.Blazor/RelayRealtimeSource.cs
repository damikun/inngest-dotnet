// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.

using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Inngest.Realtime;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Inngest.Blazor;

// Reads the app's realtime relay (MapInngestRealtime) as a browser's EventSource would, and turns the data of each event,
// a message as Inngest sends it, into a RealtimeMessage. The relay holds the subscription to Inngest, so the browser
// needs no key. A connection that ends or fails is opened again after a growing delay: the relay ends a client that falls
// 512 messages behind, and connections drop. Unlike an EventSource, a refusal such as 403 is not retried.
internal sealed class RelayRealtimeSource : IRealtimeSource
{
    internal const string HttpClientName = "Inngest.Realtime";

    // The relay sends a comment every 15 seconds, so a connection silent for three of them is taken for dead.
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan FirstRetryDelay = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromSeconds(30);

    // A connection that ends sooner than this counts as a failure, so a relay that keeps closing them is not hammered.
    private static readonly TimeSpan MinHealthyConnection = TimeSpan.FromSeconds(5);

    // A browser's fetch hands over a response only when it has ended unless it is asked to stream, and a subscription never
    // ends. .NET 10 streams by default, .NET 8 does not; this is what SetBrowserResponseStreamingEnabled of the
    // WebAssembly package sets, without a reference to that package.
    private static readonly HttpRequestOptionsKey<bool> EnableStreamingResponse = new("WebAssemblyEnableStreamingResponse");

    private readonly IHttpClientFactory _httpClients;
    private readonly IServiceProvider _services;
    private readonly RealtimeRelayOptions _options;
    private readonly JsonSerializerOptions _serializerOptions;
    private readonly TimeProvider _time;
    private readonly ILogger<RelayRealtimeSource> _logger;

    public RelayRealtimeSource(
        IHttpClientFactory httpClients,
        IServiceProvider services,
        IOptions<RealtimeRelayOptions> options,
        TimeProvider time,
        ILogger<RelayRealtimeSource> logger)
    {
        _httpClients = httpClients;
        _services = services;
        _options = options.Value;
        _time = time;
        _logger = logger;

        // Reading a type locks the options, and the app may share the instance it configured.
        _serializerOptions = new JsonSerializerOptions(_options.SerializerOptions);
    }

    public IAsyncEnumerable<RealtimeMessage<JsonElement>> SubscribeAsync(
        string channel,
        IReadOnlyList<string> topics,
        CancellationToken cancellationToken = default,
        Action<RealtimeConnectionState>? onConnectionChanged = null)
    {
        RealtimePayload.Check(channel, topics);
        return ReadAsync(
            channel,
            [.. topics],
            static () => new RealtimeMessageReader<JsonElement>(static data => data.Clone(), readDataOfEveryKind: true),
            onConnectionChanged,
            cancellationToken);
    }

    public IAsyncEnumerable<RealtimeMessage<T>> SubscribeAsync<T>(
        RealtimeTopic<T> topic,
        CancellationToken cancellationToken = default,
        Action<RealtimeConnectionState>? onConnectionChanged = null)
    {
        ArgumentNullException.ThrowIfNull(topic);
        RealtimePayload.Check(topic.Untyped);
        var typeInfo = (JsonTypeInfo<T>)_serializerOptions.GetTypeInfo(typeof(T));
        return ReadAsync(
            topic.Channel,
            [topic.Name],
            () => new RealtimeMessageReader<T>(data => data.Deserialize(typeInfo), readDataOfEveryKind: false),
            onConnectionChanged,
            cancellationToken);
    }

    private async IAsyncEnumerable<RealtimeMessage<T>> ReadAsync<T>(
        string channel,
        string[] topics,
        Func<RealtimeMessageReader<T>> createReader,
        Action<RealtimeConnectionState>? onConnectionChanged,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var url = ResolveUrl(_httpClients.CreateClient(HttpClientName), channel, topics);
        var failures = 0;
        var opened = false;
        onConnectionChanged?.Invoke(RealtimeConnectionState.Connecting);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var connectedAt = _time.GetTimestamp();

            // A client for each connection, which is how the factory keeps the handlers it pools fresh.
            var client = _httpClients.CreateClient(HttpClientName);

            // The ids of a stream mean nothing to the next connection.
            var reader = createReader();
            Exception? failure = null;
            HttpResponseMessage? response = null;
            try
            {
                response = await OpenAsync(client, url, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is HttpRequestException or IOException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
            {
                // A client that gave up waiting for the headers cancels its own request.
                failure = exception;
            }

            if (response is not null)
            {
                using (response)
                {
                    var status = response.StatusCode;
                    if (status == HttpStatusCode.NoContent)
                    {
                        // The relay tells the client to stop, as an EventSource understands it.
                        yield break;
                    }

                    if (!response.IsSuccessStatusCode)
                    {
                        // Rate limits and failures of the relay or of a proxy before it pass; a refusal does not.
                        failure = new HttpRequestException($"The realtime relay answered {(int)status}{(response.ReasonPhrase is { Length: > 0 } reason ? $" {reason}" : string.Empty)}.", null, status);
                        if (!IsTransient(status))
                        {
                            throw failure;
                        }
                    }
                    else if (response.Content.Headers.ContentType?.MediaType != "text/event-stream")
                    {
                        // Typically the page of the app, which a wrong address falls back to; an EventSource would not accept it either.
                        throw new HttpRequestException(
                            $"The realtime relay answered {response.Content.Headers.ContentType?.MediaType ?? "no content type"} instead of text/event-stream. Is the address right?",
                            null,
                            status);
                    }
                    else
                    {
                        Log.RelayConnected(_logger, channel);
                        opened = true;
                        onConnectionChanged?.Invoke(RealtimeConnectionState.Open);
                        var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                        var events = ServerSentEvents.ReadDataAsync(stream, _time, IdleTimeout, cancellationToken).GetAsyncEnumerator(cancellationToken);
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

                                failures = 0;

                                // A message whose data is not a T, as another publisher may send, is skipped rather than ending the subscription.
                                RealtimeMessage<T>? message;
                                try
                                {
                                    message = reader.Read(events.Current);
                                }
                                catch (JsonException exception)
                                {
                                    Log.RelayMessageSkipped(_logger, channel, typeof(T).Name, exception.Message);
                                    continue;
                                }

                                // The relay serves what its callback allows, which may be more than this subscription asked for.
                                if (message is not null && Array.IndexOf(topics, message.Topic) >= 0)
                                {
                                    yield return message;
                                }
                            }
                        }
                    }
                }

                if (failure is null && _time.GetElapsedTime(connectedAt) >= MinHealthyConnection)
                {
                    onConnectionChanged?.Invoke(RealtimeConnectionState.Reconnecting);
                    continue;
                }

                failure ??= new IOException("The relay ended the connection within five seconds.");
            }

            failures++;
            var delay = RetryDelay(failures);
            Log.RelayReconnecting(_logger, channel, failure!.Message, (long)delay.TotalMilliseconds);
            onConnectionChanged?.Invoke(opened ? RealtimeConnectionState.Reconnecting : RealtimeConnectionState.Connecting);
            await Task.Delay(delay, _time, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<HttpResponseMessage> OpenAsync(HttpClient client, Uri url, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        request.Options.Set(EnableStreamingResponse, true);
        return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
    }

    private Uri ResolveUrl(HttpClient client, string channel, IReadOnlyList<string> topics)
    {
        var map = _options.Url ?? throw new InvalidOperationException(
            "Set RealtimeRelayOptions.Url to the address of the app's MapInngestRealtime endpoint, for example AddInngestRealtimeRelay(options => options.Url = (channel, topics) => new Uri(\"/api/realtime/\" + Uri.EscapeDataString(channel), UriKind.Relative)).");
        var url = map(channel, topics) ?? throw new InvalidOperationException("RealtimeRelayOptions.Url returned null.");

        // The client of a WebAssembly app seldom has a base address of its own, and the endpoint is usually on the page's origin.
        if (!url.IsAbsoluteUri && client.BaseAddress is null && _services.GetService(typeof(NavigationManager)) is NavigationManager navigation)
        {
            url = new Uri(new Uri(navigation.BaseUri), url);
        }

        return url;
    }

    private static bool IsTransient(HttpStatusCode status) =>
        status is HttpStatusCode.TooManyRequests or HttpStatusCode.RequestTimeout or (HttpStatusCode)425 or >= HttpStatusCode.InternalServerError;

    private static TimeSpan RetryDelay(int failures)
    {
        var delay = FirstRetryDelay * Math.Pow(2, Math.Min(failures - 1, 10));
        delay = delay > MaxRetryDelay ? MaxRetryDelay : delay;

        // Up to a fifth less, so that clients that failed together do not come back together.
        return delay * (1 - (Random.Shared.NextDouble() * 0.2));
    }
}
