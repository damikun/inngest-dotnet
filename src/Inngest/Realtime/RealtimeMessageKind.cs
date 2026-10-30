// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

namespace Inngest;

/// <summary>What a realtime message is: published data, or a part of a published stream.</summary>
public enum RealtimeMessageKind
{
    /// <summary>Data published to the topic.</summary>
    Data,

    /// <summary>A stream starts; its chunks and its end carry the same StreamId.</summary>
    StreamStart,

    /// <summary>A piece of a stream's text, in Text.</summary>
    StreamChunk,

    /// <summary>A stream ends.</summary>
    StreamEnd,

    /// <summary>A message Inngest sends about a run, on the "$run" topic.</summary>
    Run,

    /// <summary>A kind this version of the SDK does not know.</summary>
    Other,
}
