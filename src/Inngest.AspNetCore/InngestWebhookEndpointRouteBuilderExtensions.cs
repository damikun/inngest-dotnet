// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using System.Diagnostics.CodeAnalysis;
using Inngest;
using Inngest.AspNetCore;
using Inngest.Webhooks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Microsoft.AspNetCore.Builder;

/// <summary>Maps endpoints that receive webhooks from other services as Inngest events.</summary>
public static class InngestWebhookEndpointRouteBuilderExtensions
{
    /// <summary>
    /// Receives webhooks from a provider as Inngest events. A POST is read as raw bytes, its signature is verified with
    /// the provider's scheme before anything is parsed, and its payload becomes an event whose id is the provider's id for
    /// the delivery, so that a delivery sent again is not run twice. The answer comes as soon as Inngest has accepted the
    /// event, and has no body: 200 when the event was sent, 204 when <see cref="WebhookOptions.Transform"/> dropped the
    /// delivery, 401 when the signature is missing or wrong or the timestamp is out of range, 413 when the body is over
    /// <see cref="WebhookOptions.MaxBodySize"/>, 400 when the payload is not JSON or not something an event can be made of,
    /// and 503 when Inngest does not accept the event, so that the provider delivers the webhook again. Nothing waits for
    /// runs. The endpoint allows anonymous callers, since a webhook proves who sent it with its signature, which fails
    /// closed. Rate limiting and an address allow list are the app's to add to the result.
    /// </summary>
    public static IEndpointConventionBuilder MapInngestWebhook(
        this IEndpointRouteBuilder endpoints,
        [StringSyntax("Route")] string pattern,
        WebhookProvider provider,
        WebhookOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentException.ThrowIfNullOrEmpty(pattern);
        ArgumentNullException.ThrowIfNull(provider);

        // Resolving here fails at startup, not on the first webhook, when AddInngest was not called.
        var services = endpoints.ServiceProvider;
        var client = services.GetService<IInngestClient>()
            ?? throw new InvalidOperationException("Call services.AddInngest() before app.MapInngestWebhook().");
        var receiver = new WebhookReceiver(
            provider,
            client,
            options ?? new WebhookOptions(),
            services.GetService<TimeProvider>() ?? TimeProvider.System,
            (services.GetService<ILoggerFactory>() ?? NullLoggerFactory.Instance).CreateLogger("Inngest.Webhooks"));

        return endpoints
            .MapPost(pattern, (RequestDelegate)(context => WebhookEndpoint.HandleAsync(context, receiver)))
            .WithDisplayName($"Inngest webhook ({provider.Name})")
            .ExcludeFromDescription()
            .AllowAnonymous();
    }
}
