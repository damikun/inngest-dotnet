// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.

namespace Inngest;

/// <summary>
/// What an <see cref="IWebhookVerifier"/> found. A default value is not valid, so a verifier that forgets to answer
/// rejects the request.
/// </summary>
public readonly struct WebhookVerification
{
    private WebhookVerification(bool isValid, string? deliveryId, string? reason)
    {
        IsValid = isValid;
        DeliveryId = deliveryId;
        Reason = reason;
    }

    /// <summary>True when the request is from the provider and recent enough.</summary>
    public bool IsValid { get; }

    /// <summary>
    /// The id the provider gave the delivery, when it sends it in a header, or null. It becomes the id of the event, so
    /// that a delivery sent again is not run twice. It is the same for every attempt to deliver one event.
    /// </summary>
    public string? DeliveryId { get; }

    /// <summary>
    /// Why the request was rejected, in a few words that go to the log: never a value taken from the request, and never
    /// a secret.
    /// </summary>
    public string? Reason { get; }

    /// <summary>A request from the provider. <paramref name="deliveryId"/> is the delivery id from its headers, if it has one.</summary>
    public static WebhookVerification Valid(string? deliveryId = null) => new(true, deliveryId, null);

    /// <summary>A request that is not from the provider, or is too old or too new to trust.</summary>
    public static WebhookVerification Invalid(string reason)
    {
        ArgumentException.ThrowIfNullOrEmpty(reason);
        return new WebhookVerification(false, null, reason);
    }
}
