// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

namespace Inngest;

/// <summary>
/// A realtime message as a subscriber receives it. Data holds what was published for a Data message; a published
/// stream arrives as a StreamStart, StreamChunk messages with the text in Text, and a StreamEnd, all with one StreamId.
/// </summary>
public sealed record RealtimeMessage<T>(RealtimeMessageKind Kind, string Channel, string Topic)
{
    public T? Data { get; init; }

    public string? StreamId { get; init; }

    public string? Text { get; init; }

    /// <summary>The run that published the message, when one did.</summary>
    public string? RunId { get; init; }

    /// <summary>When Inngest received the message; chunks of a stream carry none.</summary>
    public DateTimeOffset? CreatedAt { get; init; }
}
