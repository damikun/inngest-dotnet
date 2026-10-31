// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.

using System.Text.Json;

namespace Inngest.Webhooks;

// How each provider's deliveries become events. They follow the transforms in Inngest's own guide to webhooks where it
// has one: stripe/<type> with the whole event as the data and the event's id as the id, <prefix>/<type> with the data
// of the payload for Clerk and Resend. The event id is the provider's id for the delivery, which is the same for every
// attempt to deliver one event, so Inngest drops the ones after the first.
//
// The data is always the payload, which the signature covers. Names and ids come from the payload or from headers; GitHub
// and Shopify do not sign their headers, so what they name is checked for its shape but not trusted.
//
// A mapping returns null for a delivery it cannot make an event of, and the request is answered with 400. A provider that
// has delivery ids must have given one: an event without an id would never be deduplicated.
internal static class WebhookEvents
{
    // The longest name Inngest takes for an event.
    private const int MaxNameLength = 256;

    public static InngestEvent? Stripe(WebhookDelivery delivery)
    {
        var payload = delivery.Payload;
        if (payload.ValueKind != JsonValueKind.Object || ReadToken(payload, "type") is not { } type)
        {
            return null;
        }

        // The id is in the signed body, where Stripe puts the id of the event: the same for every attempt to deliver it.
        return ReadToken(payload, "id") is { } id ? Create("stripe/" + type, payload, id) : null;
    }

    public static Func<WebhookDelivery, InngestEvent?> StandardWebhooks(string prefix) => delivery =>
    {
        var payload = delivery.Payload;
        if (payload.ValueKind != JsonValueKind.Object || ReadToken(payload, "type") is not { } type || delivery.DeliveryId is not { } id)
        {
            return null;
        }

        // The data of the event is the data of the payload, as for Clerk and Resend in Inngest's guide. A payload without
        // an object as data is the data itself.
        var data = payload.TryGetProperty("data", out var inner) && inner.ValueKind == JsonValueKind.Object ? inner : payload;
        return Create($"{prefix}/{type}", data, id);
    };

    public static InngestEvent? GitHub(WebhookDelivery delivery)
    {
        // The event name and the delivery id are headers, which the signature does not cover.
        if (delivery.Payload.ValueKind != JsonValueKind.Object || Token(delivery.GetHeader("X-GitHub-Event")) is not { } kind || delivery.DeliveryId is not { } id)
        {
            return null;
        }

        return Create("github/" + kind, delivery.Payload, id);
    }

    public static InngestEvent? Shopify(WebhookDelivery delivery)
    {
        // The topic and the webhook id are headers, which the signature does not cover.
        if (delivery.Payload.ValueKind != JsonValueKind.Object || Token(delivery.GetHeader("X-Shopify-Topic")) is not { } topic || delivery.DeliveryId is not { } id)
        {
            return null;
        }

        return Create("shopify/" + topic.Replace('/', '.'), delivery.Payload, id);
    }

    // requireId is true when the provider was told which header holds the id, which is then expected in every delivery.
    public static Func<WebhookDelivery, InngestEvent?> Fixed(string name, bool requireId) => delivery =>
        delivery.Payload.ValueKind == JsonValueKind.Object && (!requireId || delivery.DeliveryId is not null)
            ? Create(name, delivery.Payload, delivery.DeliveryId)
            : null;

    // The name is made of a prefix and a part taken from the request, each within its limit, so the whole is checked too.
    private static InngestEvent? Create(string name, JsonElement data, string? id) =>
        name.Length <= MaxNameLength ? new InngestEvent(name, data) { Id = id } : null;

    private static string? ReadToken(JsonElement payload, string name) =>
        payload.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? Token(value.GetString()) : null;

    private static string? Token(string? value) => WebhookCrypto.IsToken(value) ? value : null;
}
