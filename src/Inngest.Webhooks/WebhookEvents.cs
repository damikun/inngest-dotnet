// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.

using System.Text.Json;

namespace Inngest.Webhooks;

// How each provider's deliveries become events. They follow the transforms in Inngest's own guide to webhooks where it
// has one: stripe/<type> with the whole event as the data and the event's id as the id, <prefix>/<type> with the data
// of the payload for Clerk and Resend. The event id is the provider's id for the delivery, which is the same for every
// attempt to deliver one event, so Inngest drops the ones after the first.
//
// A mapping returns null for a delivery it cannot make an event of; the request is answered with 400. Ids and names come
// from headers or from the body, so they are checked for their shape before they reach an event.
internal static class WebhookEvents
{
    public static InngestEvent? Stripe(WebhookDelivery delivery)
    {
        var payload = delivery.Payload;
        if (payload.ValueKind != JsonValueKind.Object || ReadToken(payload, "type") is not { } type)
        {
            return null;
        }

        // The id is in the signed body, where Stripe puts the id of the event: the same for every attempt to deliver it.
        return new InngestEvent("stripe/" + type, payload) { Id = ReadToken(payload, "id") };
    }

    public static Func<WebhookDelivery, InngestEvent?> StandardWebhooks(string prefix) => delivery =>
    {
        var payload = delivery.Payload;
        if (payload.ValueKind != JsonValueKind.Object || ReadToken(payload, "type") is not { } type)
        {
            return null;
        }

        // The data of the event is the data of the payload, as for Clerk and Resend in Inngest's guide. A payload without
        // an object as data is the data itself.
        var data = payload.TryGetProperty("data", out var inner) && inner.ValueKind == JsonValueKind.Object ? inner : payload;
        return new InngestEvent($"{prefix}/{type}", data) { Id = delivery.DeliveryId };
    };

    public static InngestEvent? GitHub(WebhookDelivery delivery)
    {
        if (delivery.Payload.ValueKind != JsonValueKind.Object || Token(delivery.GetHeader("X-GitHub-Event")) is not { } kind)
        {
            return null;
        }

        return new InngestEvent("github/" + kind, delivery.Payload) { Id = delivery.DeliveryId };
    }

    public static InngestEvent? Shopify(WebhookDelivery delivery)
    {
        if (delivery.Payload.ValueKind != JsonValueKind.Object || Token(delivery.GetHeader("X-Shopify-Topic")) is not { } topic)
        {
            return null;
        }

        // Most payloads do not say which shop sent them, and an app installed on many shops needs to know. Shopify's own
        // data requests carry the same property.
        var data = delivery.Payload;
        if (Token(delivery.GetHeader("X-Shopify-Shop-Domain")) is { } shop && !data.TryGetProperty("shop_domain", out _))
        {
            data = delivery.WithProperty("shop_domain", shop);
        }

        return new InngestEvent("shopify/" + topic.Replace('/', '.'), data) { Id = delivery.DeliveryId };
    }

    public static Func<WebhookDelivery, InngestEvent?> Fixed(string name) => delivery =>
        delivery.Payload.ValueKind == JsonValueKind.Object
            ? new InngestEvent(name, delivery.Payload) { Id = delivery.DeliveryId }
            : null;

    private static string? ReadToken(JsonElement payload, string name) =>
        payload.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? Token(value.GetString()) : null;

    private static string? Token(string? value) => WebhookCrypto.IsToken(value) ? value : null;
}
