// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using Inngest.Configuration;
using Inngest.Execution;

namespace Inngest.Realtime;

internal sealed class InngestRealtime(IRealtimeApi api, InngestSettings settings) : IInngestRealtime
{
    public Task PublishAsync<T>(RealtimeTopic topic, T data, CancellationToken cancellationToken = default)
    {
        RealtimeMessage.Check(topic);
        return api.PublishAsync(topic, Invocation.AmbientContext?.RunId, RealtimeMessage.Body(data, settings.SerializerOptions), cancellationToken);
    }

    public async Task<RealtimeToken> GetSubscriptionTokenAsync(string channel, IReadOnlyList<string> topics, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(channel);
        ArgumentNullException.ThrowIfNull(topics);
        foreach (var topic in topics)
        {
            ArgumentException.ThrowIfNullOrEmpty(topic, nameof(topics));
        }

        var key = await api.GetTokenAsync(channel, topics, cancellationToken).ConfigureAwait(false);
        return new RealtimeToken(channel, [.. topics], key) { ApiBaseUrl = settings.ApiBaseUrl.AbsoluteUri };
    }
}
