// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using Inngest.Internal;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Inngest.Client;

internal sealed class InngestClient : IInngestClient
{
    internal const int MaxAttempts = 5;

    // The dev server accepts any non-empty key, so a client without one can still send in dev mode.
    internal const string PlaceholderEventKey = "NO_EVENT_KEY_SET";

    private const int BaseDelayMilliseconds = 100;
    private const int InitialBodyCapacity = 1024;

    private static readonly SendEventsResult EmptyResult = new([]);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly InngestSettings _settings;
    private readonly TimeProvider _timeProvider;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<InngestClient> _logger;
    private readonly ISignalApi _signals;
    private readonly bool _mayHaveMiddleware;
    private readonly Uri? _sendUrl;
    private readonly JsonWriterOptions _writerOptions;

    public InngestClient(
        IHttpClientFactory httpClientFactory,
        InngestSettings settings,
        TimeProvider timeProvider,
        IServiceScopeFactory scopeFactory,
        ILogger<InngestClient> logger,
        IServiceProviderIsService? serviceProviderIsService = null,
        ISignalApi? signals = null)
    {
        _httpClientFactory = httpClientFactory;
        _settings = settings;
        _timeProvider = timeProvider;
        _scopeFactory = scopeFactory;
        _logger = logger;
        _signals = signals ?? new SignalApi(httpClientFactory, settings, NullLogger<SignalApi>.Instance);

        // Containers cannot change after they are built, so this is known now and public sends skip the scope
        // entirely when there is nothing to resolve. A container that cannot tell is assumed to have middleware.
        _mayHaveMiddleware = serviceProviderIsService?.IsService(typeof(InngestMiddleware)) ?? true;

        var eventKey = settings.EventKey ?? (settings.IsDev ? PlaceholderEventKey : null);
        _sendUrl = eventKey is null ? null : new Uri(settings.EventApiBaseUrl, "e/" + Uri.EscapeDataString(eventKey));
        _writerOptions = new JsonWriterOptions { Encoder = settings.SerializerOptions.Encoder };
    }

    public string AppId => _settings.AppId;

    public Task<SendEventsResult> SendAsync(InngestEvent @event, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(@event);
        return SendAsync([@event], null, null, cancellationToken);
    }

    public Task<SendEventsResult> SendAsync(IEnumerable<InngestEvent> events, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(events);
        return SendAsync(events as IReadOnlyList<InngestEvent> ?? [.. events], null, null, cancellationToken);
    }

    public Task<SendEventsResult> SendAsync(InngestEvent @event, SendOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(@event);
        ArgumentNullException.ThrowIfNull(options);
        return SendAsync([@event], null, null, EnvironmentOf(options), cancellationToken);
    }

    public Task<SendEventsResult> SendAsync(IEnumerable<InngestEvent> events, SendOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(options);
        return SendAsync(events as IReadOnlyList<InngestEvent> ?? [.. events], null, null, EnvironmentOf(options), cancellationToken);
    }

    public Task<string?> SendSignalAsync(InngestSignal signal, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(signal);
        ArgumentException.ThrowIfNullOrEmpty(signal.Name);
        return PostSignalAsync(signal, null, cancellationToken);
    }

    public Task<string?> SendSignalAsync(InngestSignal signal, SendOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(signal);
        ArgumentException.ThrowIfNullOrEmpty(signal.Name);
        ArgumentNullException.ThrowIfNull(options);
        return PostSignalAsync(signal, EnvironmentOf(options), cancellationToken);
    }

    // Used by step.sendEvent: middleware sees the run's context and is resolved from the invocation's scope.
    internal Task<SendEventsResult> SendAsync(
        IReadOnlyList<InngestEvent> events,
        FunctionContext? context,
        IServiceProvider? services,
        CancellationToken cancellationToken) =>
        SendAsync(events, context, services, null, cancellationToken);

    // environment, when set, replaces the app's environment for this send.
    private async Task<SendEventsResult> SendAsync(
        IReadOnlyList<InngestEvent> events,
        FunctionContext? context,
        IServiceProvider? services,
        string? environment,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(events);

        if (events.Count == 0)
        {
            Log.NoEventsToSend(_logger);
            return EmptyResult;
        }

        var url = _sendUrl ?? throw new InngestException(
            "Failed to send events: no event key is configured. " +
            "Set the INNGEST_EVENT_KEY environment variable or InngestOptions.EventKey.");

        // Taken once per call, so every retry and every middleware pass sends the same time and the same seed.
        var now = _timeProvider.GetUtcNow();
        var seed = CreateEventIdSeed(now);

        if (!_mayHaveMiddleware && context?.Middleware is not { Count: > 0 })
        {
            return await PostAsync(url, environment, events, now, seed, cancellationToken).ConfigureAwait(false);
        }

        if (services is not null)
        {
            return await RunMiddlewareAsync(services, context, url, environment, events, now, seed, cancellationToken).ConfigureAwait(false);
        }

        await using var scope = _scopeFactory.CreateAsyncScope();
        return await RunMiddlewareAsync(scope.ServiceProvider, context, url, environment, events, now, seed, cancellationToken).ConfigureAwait(false);
    }

    // 100 ms, 200 ms, 400 ms and so on after each failed attempt, plus up to 100 ms of jitter so that clients that
    // failed together do not retry together.
    internal static TimeSpan GetBackoffDelay(int failedAttempts, double jitter) =>
        TimeSpan.FromMilliseconds((BaseDelayMilliseconds << (failedAttempts - 1)) + (BaseDelayMilliseconds * jitter));

    // Inngest derives the ids of the events from this, so a retried send creates the same events instead of copies.
    private static string CreateEventIdSeed(DateTimeOffset now)
    {
        Span<byte> entropy = stackalloc byte[10];
        RandomNumberGenerator.Fill(entropy);
        return $"{now.ToUnixTimeMilliseconds()},{Convert.ToBase64String(entropy)}";
    }

    // Like inngest-js, an empty environment leaves the app's in place.
    private static string? EnvironmentOf(SendOptions options) =>
        string.IsNullOrEmpty(options.Environment) ? null : options.Environment;

    private static void ValidateName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("An event needs a name.");
        }

        if (name.StartsWith("inngest/", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"The event name '{name}' is reserved: names starting with 'inngest/' belong to Inngest.");
        }
    }

    // The first registered middleware is the outermost, and the whole send, retries included, is one call to it.
    private Task<SendEventsResult> RunMiddlewareAsync(
        IServiceProvider services,
        FunctionContext? context,
        Uri url,
        string? environment,
        IReadOnlyList<InngestEvent> events,
        DateTimeOffset now,
        string seed,
        CancellationToken cancellationToken)
    {
        // A run's context carries the middleware of its call, which includes the function's own.
        var resolved = context?.Middleware ?? services.GetServices<InngestMiddleware>();
        var middleware = resolved as IReadOnlyList<InngestMiddleware> ?? [.. resolved];

        return Next(0, events);

        Task<SendEventsResult> Next(int index, IReadOnlyList<InngestEvent> current) =>
            index == middleware.Count
                ? PostAsync(url, environment, current, now, seed, cancellationToken)
                : middleware[index].WrapSendEventAsync(context, current, next => Next(index + 1, next), cancellationToken);
    }

    private async Task<SendEventsResult> PostAsync(
        Uri url,
        string? environment,
        IReadOnlyList<InngestEvent> events,
        DateTimeOffset now,
        string seed,
        CancellationToken cancellationToken)
    {
        // Middleware may have dropped every event.
        if (events.Count == 0)
        {
            return EmptyResult;
        }

        using var body = new PooledBufferWriter(InitialBodyCapacity);
        WriteBody(body, events, now.ToUnixTimeMilliseconds());

        for (var attempt = 1; ; attempt++)
        {
            var outcome = await TryPostAsync(url, environment, body.WrittenMemory, seed, cancellationToken).ConfigureAwait(false);
            if (outcome.Result is { } result)
            {
                Log.SentEvents(_logger, events.Count);
                return result;
            }

            var error = outcome.Error!;
            if (attempt == MaxAttempts || !outcome.IsRetryable)
            {
                throw error;
            }

            var delay = GetBackoffDelay(attempt, Random.Shared.NextDouble());
            Log.RetryingSend(_logger, attempt, MaxAttempts, error.Message, (int)delay.TotalMilliseconds);
            await Task.Delay(delay, _timeProvider, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<SendOutcome> TryPostAsync(
        Uri url,
        string? environment,
        ReadOnlyMemory<byte> body,
        string seed,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new ReadOnlyMemoryContent(body) };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        request.AddSdkHeaders(environment ?? _settings.Environment);
        request.Headers.TryAddWithoutValidation(Headers.EventIdSeed, seed);

        try
        {
            var client = _httpClientFactory.CreateClient(InngestServiceCollectionExtensions.HttpClientName);
            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var content = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            return ReadResponse((int)response.StatusCode, content);
        }
        catch (HttpRequestException exception)
        {
            return SendOutcome.Retry(new InngestException($"Could not reach Inngest to send events: {exception.Message}", exception));
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            // HttpClient reports its own timeout this way.
            return SendOutcome.Retry(new InngestException("Sending events to Inngest timed out.", exception));
        }
    }

    // The body decides, not the HTTP status: a send that succeeded for only some events still answers with an
    // error status in the body.
    private static SendOutcome ReadResponse(int httpStatus, byte[] content)
    {
        EventApiResponse? reply;
        try
        {
            reply = JsonSerializer.Deserialize(content, ClientJsonContext.Default.EventApiResponse);
        }
        catch (JsonException)
        {
            reply = null;
        }

        if (reply is { Status: 200, Error: null or "" })
        {
            return SendOutcome.Success(new SendEventsResult(reply.Ids ?? []));
        }

        var status = reply is { Status: > 0 } ? reply.Status : httpStatus;
        var message = reply?.Error is { Length: > 0 } error ? error : DescribeStatus(status, content);
        var exception = new InngestException($"Inngest API error: {status} {message}", status);

        // Client errors will fail the same way again, so only transient failures are worth another attempt.
        return new SendOutcome(null, exception, status is < 400 or >= 500 or 408 or 425 or 429);
    }

    private static string DescribeStatus(int status, byte[] content) => status switch
    {
        400 => "Cannot process event payload",
        401 or 404 => "Event key not found",
        403 => "Forbidden",
        409 or 412 => "Event transformation failed",
        413 => "Event payload too large",
        500 => "Internal server error",
        _ => ApiHttp.ToText(content) ?? "Unexpected response",
    };

    private void WriteBody(PooledBufferWriter buffer, IReadOnlyList<InngestEvent> events, long defaultTimestamp)
    {
        using var writer = new Utf8JsonWriter(buffer, _writerOptions);
        writer.WriteStartArray();
        for (var i = 0; i < events.Count; i++)
        {
            WriteEvent(writer, events[i], defaultTimestamp);
        }

        writer.WriteEndArray();
    }

    private void WriteEvent(Utf8JsonWriter writer, InngestEvent? @event, long defaultTimestamp)
    {
        if (@event is null)
        {
            throw new ArgumentException("The events must not contain null.");
        }

        ValidateName(@event.Name);
        JsonPayload.EnsureObject(@event.Data, _settings.SerializerOptions, "data", "event", @event.Name);
        JsonPayload.EnsureObject(@event.User, _settings.SerializerOptions, "user", "event", @event.Name);

        writer.WriteStartObject();
        writer.WriteString("name"u8, @event.Name);

        writer.WritePropertyName("data"u8);
        if (@event.Data is null)
        {
            writer.WriteStartObject();
            writer.WriteEndObject();
        }
        else
        {
            WriteValue(writer, @event.Data);
        }

        if (@event.User is not null)
        {
            writer.WritePropertyName("user"u8);
            WriteValue(writer, @event.User);
        }

        if (!string.IsNullOrEmpty(@event.Id))
        {
            writer.WriteString("id"u8, @event.Id);
        }

        writer.WriteNumber("ts"u8, @event.Timestamp?.ToUnixTimeMilliseconds() ?? defaultTimestamp);

        if (!string.IsNullOrEmpty(@event.Version))
        {
            writer.WriteString("v"u8, @event.Version);
        }

        writer.WriteEndObject();
    }

    private void WriteValue(Utf8JsonWriter writer, object value) =>
        JsonSerializer.Serialize(writer, value, _settings.SerializerOptions.GetTypeInfo(value.GetType()));

    private async Task<string?> PostSignalAsync(InngestSignal signal, string? environment, CancellationToken cancellationToken)
    {
        using var body = new PooledBufferWriter();
        using (var writer = new Utf8JsonWriter(body, _writerOptions))
        {
            writer.WriteStartObject();
            writer.WriteString("signal"u8, signal.Name);
            if (signal.Data is not null)
            {
                writer.WritePropertyName("data"u8);
                WriteValue(writer, signal.Data);
            }

            writer.WriteEndObject();
        }

        var runId = await _signals.SendAsync(signal.Name, body.WrittenMemory, environment, cancellationToken).ConfigureAwait(false);
        if (runId is null)
        {
            Log.NoRunWaitsForSignal(_logger, signal.Name);
        }
        else
        {
            Log.SentSignal(_logger, signal.Name, runId);
        }

        return runId;
    }

    private readonly record struct SendOutcome(SendEventsResult? Result, InngestException? Error, bool IsRetryable)
    {
        public static SendOutcome Success(SendEventsResult result) => new(result, null, false);

        public static SendOutcome Retry(InngestException error) => new(null, error, true);
    }
}
