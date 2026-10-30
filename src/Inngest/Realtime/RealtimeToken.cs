// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

namespace Inngest;

/// <summary>
/// Lets a browser subscribe to the topics of one channel. Serialized with the usual camelCase names, it is the token
/// object the TypeScript SDK's useRealtime and subscribe take, so an endpoint can return it as it is. Key is a
/// short-lived JWT; mint one per viewer after checking that the viewer may read the channel.
/// </summary>
public sealed record RealtimeToken(string Channel, IReadOnlyList<string> Topics, string Key)
{
    /// <summary>Where the browser connects: the API address of the Inngest the app uses.</summary>
    public string? ApiBaseUrl { get; init; }
}
