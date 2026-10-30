// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

namespace Inngest;

/// <summary>How MapAsync, ForEachAsync and the other fan-out methods start the items of a list.</summary>
public sealed record FanOutOptions
{
    /// <summary>
    /// How many items run at the same time, or how many chunks for the methods that run chunks in child runs. Null
    /// starts them all at once, as Task.WhenAll does. A new item starts when Inngest calls the function again with a
    /// result: after every item in flight has finished by default, or after each one in
    /// <see cref="ParallelMode.Race"/>.
    /// </summary>
    public int? MaxParallelism { get; init; }

    /// <summary>
    /// When Inngest calls the function again while items are in flight. Null leaves it to the enclosing
    /// ParallelAsync callback, which is <see cref="ParallelMode.Wait"/> outside one.
    /// </summary>
    public ParallelMode? ParallelMode { get; init; }

    /// <summary>
    /// How long a chunk's run may take, for the methods that run chunks in child runs. When it passes, the chunk's
    /// step fails and the chunk's run keeps going. Null leaves it to Inngest, whose default is long.
    /// </summary>
    public TimeSpan? ChunkTimeout { get; init; }
}
