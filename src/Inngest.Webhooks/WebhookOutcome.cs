// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.

namespace Inngest.Webhooks;

// What became of a delivery, and so what the provider is told. The answers carry no body: a caller that cannot prove
// who it is learns nothing from them.
internal enum WebhookOutcome
{
    // The event was sent and Inngest accepted it.
    Accepted,

    // The transform dropped the delivery, so there is nothing to wait for.
    Skipped,

    // No signature matches, or the timestamp is out of range.
    Unauthorized,

    // The body is larger than the limit.
    PayloadTooLarge,

    // The signature is valid but the body is not JSON, or is not something an event can be made of.
    BadRequest,

    // Inngest did not accept the event in time, or at all. The provider delivers the webhook again, and the delivery id
    // makes the new attempt a duplicate if the first one got through after all.
    Unavailable,

    // The verifier or the transform threw.
    Failed,
}

internal static class WebhookOutcomes
{
    public static int StatusCode(this WebhookOutcome outcome) => outcome switch
    {
        WebhookOutcome.Accepted => 200,
        WebhookOutcome.Skipped => 204,
        WebhookOutcome.Unauthorized => 401,
        WebhookOutcome.PayloadTooLarge => 413,
        WebhookOutcome.BadRequest => 400,
        WebhookOutcome.Unavailable => 503,
        _ => 500,
    };
}
