// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.

using System.Text.Json;

namespace Inngest;

/// <summary>
/// A webhook whose signature has been verified, as the code that turns it into an event sees it. The host creates one
/// for every request, and a test can create one to try a <see cref="WebhookOptions.Transform"/>.
/// </summary>
/// <remarks>
/// The delivery lives as long as the request is handled, and so does its <see cref="Payload"/>, which refers to the body
/// the host read. Do not keep either afterwards. The event that is sent does not depend on them: a <c>JsonElement</c> as
/// its data or user is copied before the send, so the event can be kept, as the test host and middleware do. A
/// <c>JsonElement</c> of the payload inside another object is not copied: call <c>Payload.Clone()</c> to put one there.
/// </remarks>
public sealed class WebhookDelivery
{
    private readonly IWebhookRequest? _request;

    /// <summary>Describes a delivery, for the host that received it or for a test.</summary>
    /// <param name="provider">The name of the <see cref="WebhookProvider"/> that verified the delivery.</param>
    /// <param name="deliveryId">The id the provider gave the delivery in a header, or null.</param>
    /// <param name="payload">The body, parsed.</param>
    /// <param name="request">The request, for its headers and services. Null for a delivery that has neither.</param>
    public WebhookDelivery(string provider, string? deliveryId, JsonElement payload, IWebhookRequest? request = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        Provider = provider;
        DeliveryId = deliveryId;
        Payload = payload;
        _request = request;
    }

    /// <summary>The name of the <see cref="WebhookProvider"/> that verified the delivery, such as <c>stripe</c>.</summary>
    public string Provider { get; }

    /// <summary>
    /// The id the provider gave the delivery in a header, or null when it sends none in a header. The default event takes
    /// its id from here, or from the payload for Stripe. For GitHub and Shopify it comes from a header that the signature
    /// does not cover.
    /// </summary>
    public string? DeliveryId { get; }

    /// <summary>The body, parsed. Its signature has been verified, so it is what the provider sent. Valid while the request is handled.</summary>
    public JsonElement Payload { get; }

    /// <summary>The services of the request, or null outside a web host.</summary>
    public IServiceProvider? Services => _request?.Services;

    /// <summary>
    /// The value of a request header, or null when it is absent. Names are case-insensitive. Most providers do not sign
    /// their headers, so a header is not proof that the provider set it: whoever holds one captured delivery can send it
    /// again with other headers.
    /// </summary>
    public string? GetHeader(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        return _request?.GetHeader(name);
    }
}
