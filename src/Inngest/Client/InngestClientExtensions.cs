// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

namespace Inngest;

public static class InngestClientExtensions
{
    /// <summary>
    /// Sends the data as an event named T.EventName. Build an InngestEvent instead to set its id, user or timestamp.
    /// </summary>
    public static Task<SendEventsResult> SendAsync<T>(this IInngestClient client, T data, CancellationToken cancellationToken = default)
        where T : IInngestEventData
    {
        ArgumentNullException.ThrowIfNull(client);
        return client.SendAsync(InngestEvent.From(data), cancellationToken);
    }

    public static Task<SendEventsResult> SendAsync<T>(this IInngestClient client, IEnumerable<T> data, CancellationToken cancellationToken = default)
        where T : IInngestEventData
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(data);
        return client.SendAsync([.. data.Select(InngestEvent.From)], cancellationToken);
    }
}
