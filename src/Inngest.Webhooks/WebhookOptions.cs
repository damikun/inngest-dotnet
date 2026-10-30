// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.

namespace Inngest;

/// <summary>Settings of a webhook endpoint that are the same for every provider.</summary>
public sealed record WebhookOptions
{
    /// <summary>
    /// The largest body accepted, in bytes. A larger request is answered with 413 without being read to its end. It
    /// bounds what an anonymous caller can make the app hold, since the body is read in full before the signature can
    /// be checked. 1 MiB by default. Inngest refuses events over 256 KiB on its free plan and 3 MiB on Pro and Business,
    /// so a limit above what the plan takes only turns a 413 here into a failed send.
    /// </summary>
    public int MaxBodySize { get; init; } = 1024 * 1024;

    /// <summary>
    /// How long to wait for Inngest to accept the event before the request is answered with 503, which makes the provider
    /// deliver it again. Providers wait only seconds for an answer (Shopify five), and sending can take longer than that
    /// when Inngest is slow, because the client retries a failed send. Four seconds by default.
    /// </summary>
    public TimeSpan SendTimeout { get; init; } = TimeSpan.FromSeconds(4);

    /// <summary>
    /// Changes the event before it is sent, or drops the delivery. It gets the delivery and the event the provider's
    /// conventions made, and returns the event to send, such as <c>@event with { Name = "billing/paid" }</c>, or null to
    /// skip the delivery, which is answered with 204. It runs on the request, after the signature has been verified:
    /// keep it quick and let a function do the work.
    /// </summary>
    public Func<WebhookDelivery, InngestEvent, InngestEvent?>? Transform { get; init; }
}
