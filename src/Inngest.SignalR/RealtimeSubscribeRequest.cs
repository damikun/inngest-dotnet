// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.

using Microsoft.AspNetCore.SignalR;

namespace Inngest;

/// <summary>
/// A SignalR client asking to receive these topics of a channel, as <see cref="InngestSignalROptions.Authorize"/> sees
/// it: who asks, what for, and the services to check it with.
/// </summary>
public sealed class RealtimeSubscribeRequest
{
    /// <summary>
    /// Makes a request, which the bridge does for each client that asks to join, and a test can do to call
    /// <see cref="InngestSignalROptions.Authorize"/> directly.
    /// </summary>
    /// <param name="caller">The client's connection.</param>
    /// <param name="requested">What the client asked for.</param>
    /// <param name="services">The services the callback resolves what it needs from.</param>
    /// <param name="cancellationToken">Cancelled when the client disconnects.</param>
    public RealtimeSubscribeRequest(HubCallerContext caller, RealtimeSubscription requested, IServiceProvider services, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(requested);
        ArgumentNullException.ThrowIfNull(services);
        Caller = caller;
        Requested = requested;
        Services = services;
        CancellationToken = cancellationToken;
    }

    /// <summary>The client's connection: its <see cref="HubCallerContext.User"/> and <see cref="HubCallerContext.ConnectionId"/>.</summary>
    public HubCallerContext Caller { get; }

    /// <summary>What the client asked for. Nothing in it has been checked beyond being present.</summary>
    public RealtimeSubscription Requested { get; }

    /// <summary>
    /// A scope of its own for this request, so that scoped services such as a database context can be resolved from it
    /// and are disposed when the callback returns.
    /// </summary>
    public IServiceProvider Services { get; }

    /// <summary>Cancelled when the client disconnects.</summary>
    public CancellationToken CancellationToken { get; }
}
