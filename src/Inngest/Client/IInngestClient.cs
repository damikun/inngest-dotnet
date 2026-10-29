// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

namespace Inngest;

public interface IInngestClient
{
    /// <summary>The id of the app this client belongs to.</summary>
    string AppId { get; }

    /// <summary>Sends one event to Inngest.</summary>
    Task<SendEventsResult> SendAsync(InngestEvent @event, CancellationToken cancellationToken = default);

    /// <summary>Sends several events to Inngest in a single request.</summary>
    Task<SendEventsResult> SendAsync(IEnumerable<InngestEvent> events, CancellationToken cancellationToken = default);

    /// <summary>Sends one event to Inngest with options for this call, such as another environment.</summary>
    Task<SendEventsResult> SendAsync(InngestEvent @event, SendOptions options, CancellationToken cancellationToken = default) =>
        SendAsync([@event], options, cancellationToken);

    /// <summary>Sends several events to Inngest in a single request with options for this call, such as another environment.</summary>
    Task<SendEventsResult> SendAsync(IEnumerable<InngestEvent> events, SendOptions options, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException($"{GetType().Name} does not implement SendAsync with SendOptions.");

    /// <summary>Sends a signal and returns the id of the run it resumed, or null when no run waits for it.</summary>
    Task<string?> SendSignalAsync(InngestSignal signal, CancellationToken cancellationToken = default) =>
        SendSignalAsync(signal, new SendOptions(), cancellationToken);

    /// <summary>Sends a signal with options for this call, such as another environment, and returns the id of the run it resumed, or null.</summary>
    Task<string?> SendSignalAsync(InngestSignal signal, SendOptions options, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException($"{GetType().Name} does not implement SendSignalAsync.");
}
