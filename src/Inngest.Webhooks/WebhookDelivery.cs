// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.

using System.Buffers;
using System.Text.Json;

namespace Inngest;

/// <summary>
/// A webhook whose signature has been verified, as the code that turns it into an event sees it. It is valid while the
/// request is handled; do not keep it or its <see cref="Payload"/> afterwards.
/// </summary>
public sealed class WebhookDelivery
{
    private readonly IWebhookRequest _request;
    private List<JsonDocument>? _documents;

    internal WebhookDelivery(string provider, string? deliveryId, JsonElement payload, IWebhookRequest request)
    {
        Provider = provider;
        DeliveryId = deliveryId;
        Payload = payload;
        _request = request;
    }

    /// <summary>The name of the <see cref="WebhookProvider"/> that verified the delivery, such as <c>stripe</c>.</summary>
    public string Provider { get; }

    /// <summary>
    /// The id the provider gave the delivery in a header, or null when it sends none in a header. The default event takes
    /// its id from here, or from the payload for Stripe.
    /// </summary>
    public string? DeliveryId { get; }

    /// <summary>The body, parsed. Its signature has been verified, so it is what the provider sent.</summary>
    public JsonElement Payload { get; }

    /// <summary>The services of the request, or null outside a web host.</summary>
    public IServiceProvider? Services => _request.Services;

    /// <summary>
    /// The value of a request header, or null when it is absent. Names are case-insensitive. Most providers do not sign
    /// their headers, so one that only the provider could have set is not proof of it.
    /// </summary>
    public string? GetHeader(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        return _request.GetHeader(name);
    }

    // The payload with a string property added, for providers that say in a header what the payload does not. The
    // document that holds it lives as long as the delivery.
    internal JsonElement WithProperty(string name, string value)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            foreach (var property in Payload.EnumerateObject())
            {
                property.WriteTo(writer);
            }

            writer.WriteString(name, value);
            writer.WriteEndObject();
        }

        var document = JsonDocument.Parse(buffer.WrittenMemory);
        (_documents ??= []).Add(document);
        return document.RootElement;
    }

    internal void Release()
    {
        if (_documents is null)
        {
            return;
        }

        foreach (var document in _documents)
        {
            document.Dispose();
        }

        _documents = null;
    }
}
