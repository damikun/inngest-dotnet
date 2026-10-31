// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.

using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Inngest.Webhooks;

// Handles one webhook endpoint, for any host: verify, parse, make the event, send it, say what to answer. Hosts read the
// body within MaxBodySize and call ReceiveAsync; what they answer is the status code of the outcome.
internal sealed class WebhookReceiver
{
    private readonly WebhookProvider _provider;
    private readonly IInngestClient _client;
    private readonly WebhookOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;

    public WebhookReceiver(WebhookProvider provider, IInngestClient client, WebhookOptions options, TimeProvider time, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaxBodySize, nameof(options) + "." + nameof(WebhookOptions.MaxBodySize));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.SendTimeout, TimeSpan.Zero, nameof(options) + "." + nameof(WebhookOptions.SendTimeout));

        // What a CancellationTokenSource can wait for.
        ArgumentOutOfRangeException.ThrowIfGreaterThan(options.SendTimeout, TimeSpan.FromMilliseconds(uint.MaxValue - 1), nameof(options) + "." + nameof(WebhookOptions.SendTimeout));
        _provider = provider;
        _client = client;
        _options = options;
        _time = time;
        _logger = logger;
    }

    public int MaxBodySize => _options.MaxBodySize;

    // For a host that found out, before it had read the body, that the body is over the limit.
    public WebhookOutcome BodyTooLarge()
    {
        Log.BodyTooLarge(_logger, _provider.Name, _options.MaxBodySize);
        return WebhookOutcome.PayloadTooLarge;
    }

    public async ValueTask<WebhookOutcome> ReceiveAsync(IWebhookRequest request, CancellationToken cancellationToken)
    {
        var provider = _provider.Name;
        if (request.Body.Length > _options.MaxBodySize)
        {
            return BodyTooLarge();
        }

        // Nothing is parsed before the signature has been checked.
        WebhookVerification verification;
        try
        {
            verification = _provider.Verifier.Verify(request, _time.GetUtcNow());
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Log.Failed(_logger, exception, provider);
            return WebhookOutcome.Failed;
        }

        if (!verification.IsValid)
        {
            Log.Rejected(_logger, provider, verification.Reason ?? "the request was not verified");
            return WebhookOutcome.Unauthorized;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(request.Body);
        }
        catch (JsonException)
        {
            Log.NotJson(_logger, provider);
            return WebhookOutcome.BadRequest;
        }

        // The document reads the body in place, and the event is made of its elements, so it lives until the send is over.
        using (document)
        {
            var delivery = new WebhookDelivery(provider, verification.DeliveryId, document.RootElement, request);
            return await SendAsync(delivery, cancellationToken).ConfigureAwait(false);
        }
    }

    private async ValueTask<WebhookOutcome> SendAsync(WebhookDelivery delivery, CancellationToken cancellationToken)
    {
        var provider = _provider.Name;
        InngestEvent? @event;
        try
        {
            @event = _provider.CreateEvent(delivery);
            if (@event is null)
            {
                Log.NotMapped(_logger, provider);
                return WebhookOutcome.BadRequest;
            }

            if (_options.Transform is { } transform)
            {
                @event = transform(delivery, @event);
                if (@event is null)
                {
                    Log.Skipped(_logger, provider);
                    return WebhookOutcome.Skipped;
                }
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Log.Failed(_logger, exception, provider);
            return WebhookOutcome.Failed;
        }

        // What the event holds of the request's document is copied, because the event may be kept after the request: by the
        // test host, or by middleware. The pooled body goes back to the pool as soon as the request ends.
        @event = Owned(@event);
        if (!CanBeWritten(@event))
        {
            Log.EventRefused(_logger, provider, "its payload cannot be written as JSON, for example because a string holds an unpaired surrogate");
            return WebhookOutcome.Refused;
        }

        using var timeout = new CancellationTokenSource(_options.SendTimeout, _time);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            await _client.SendAsync(@event, limit.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The provider hung up. A provider that retries delivers the webhook again, so nothing is lost.
            throw;
        }
        catch (OperationCanceledException)
        {
            Log.SendTimedOut(_logger, provider, _options.SendTimeout);
            return WebhookOutcome.Unavailable;
        }
        catch (InngestException exception) when (exception.StatusCode is { } status && IsPermanent(status))
        {
            // Inngest refused the event itself, such as an event that is too large. A copy of it fails the same way.
            Log.EventRefused(_logger, provider, exception.Message);
            return status == StatusCodes.PayloadTooLarge ? WebhookOutcome.PayloadTooLarge : WebhookOutcome.Refused;
        }
        catch (ArgumentException exception)
        {
            // The client refuses an event it cannot send: a reserved name, or data that is not a JSON object. Sending it
            // again would fail the same way. The message names the problem, not the payload.
            Log.EventRefused(_logger, provider, exception.Message);
            return WebhookOutcome.Refused;
        }
        catch (Exception exception)
        {
            Log.SendFailed(_logger, exception, provider);
            return WebhookOutcome.Unavailable;
        }

        Log.Sent(_logger, provider, @event.Name, @event.Id);
        return WebhookOutcome.Accepted;
    }

    // A client error says the request is wrong, and the same request is wrong again, except when the server only asks to wait
    // (the statuses the client itself retries).
    private static bool IsPermanent(int status) => status is >= 400 and < 500 and not (408 or 425 or 429);

    private static InngestEvent Owned(InngestEvent @event) =>
        @event.Data is JsonElement || @event.User is JsonElement
            ? @event with { Data = Owned(@event.Data), User = Owned(@event.User) }
            : @event;

    private static object? Owned(object? value) => value is JsonElement element ? element.Clone() : value;

    // A string with an unpaired surrogate escape parses, and then no client can write it as JSON, so it would fail every
    // attempt. Writing it once here tells that apart from a send that failed.
    private static bool CanBeWritten(InngestEvent @event) => CanBeWritten(@event.Data) && CanBeWritten(@event.User);

    private static bool CanBeWritten(object? value)
    {
        if (value is not JsonElement element)
        {
            return true;
        }

        try
        {
            using var writer = new Utf8JsonWriter(Stream.Null);
            element.WriteTo(writer);
            return true;
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException or JsonException)
        {
            return false;
        }
    }

    private static class StatusCodes
    {
        public const int PayloadTooLarge = 413;
    }
}
