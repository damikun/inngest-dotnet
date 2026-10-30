// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

namespace Inngest;

/// <summary>
/// The data of the event that starts the run of one chunk, which MapInChunksAsync sends to the app's
/// <see cref="FanOutFunction{TItem, TResult}"/>. The Inngest UI shows it as the input of the chunk's run. Apps that
/// serialize with a source-generated JsonSerializerContext add this type for their item type to it.
/// </summary>
/// <param name="Offset">The position of the first item of the chunk in the whole list.</param>
/// <param name="Items">The items of the chunk.</param>
/// <param name="FailFast">
/// Whether the chunk stops starting items once one has failed for good. Otherwise every item runs.
/// </param>
public sealed record FanOutChunk<TItem>(int Offset, IReadOnlyList<TItem> Items, bool FailFast);
