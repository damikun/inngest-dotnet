// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using Inngest.Steps;

namespace Inngest;

/// <summary>
/// The function that runs one chunk of a list for MapInChunksAsync and MapOutcomesInChunksAsync: it calls
/// <see cref="ProcessAsync"/> for each item of the chunk, as a step of its own with the id "item[index]", and returns the
/// outcome of each item. Derive from it, implement ProcessAsync, and register the class like any function class, with
/// an id and no trigger, since only the fan-out starts it. The function's options apply to the chunk's run: its
/// retries to each item's step, its concurrency limits to all the steps of all the chunks.
/// </summary>
public abstract class FanOutFunction<TItem, TResult> : InngestFunction<FanOutChunk<TItem>, IReadOnlyList<FanOutOutcome<TResult>>>
{
    /// <summary>
    /// How many items of a chunk run at the same time. Null, the default, runs them all at once. The chunk size and the
    /// concurrency limit of the function bound the work as well.
    /// </summary>
    protected virtual int? MaxParallelism => null;

    /// <summary>
    /// Processes one item, as the handler of a step: it is retried when it throws, the result is saved, and it does not
    /// run again when the chunk's run is replayed. It cannot call steps.
    /// </summary>
    protected abstract Task<TResult> ProcessAsync(TItem item, CancellationToken cancellationToken);

    public sealed override async Task<IReadOnlyList<FanOutOutcome<TResult>>> ExecuteAsync(
        FunctionContext<FanOutChunk<TItem>> context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var chunk = context.Event.Data;
        if (chunk?.Items is not { } items)
        {
            throw new NonRetriableException(
                $"{GetType().Name} runs a chunk of a list. Start it with MapInChunksAsync or MapOutcomesInChunksAsync, which send the chunk as the event data.");
        }

        var step = context.Step;
        var run = await FanOutRun<TResult>.RunAsync(
            items.Count,
            FanOutExtensions.WidthOf(MaxParallelism, items.Count, nameof(MaxParallelism)),
            chunk.FailFast,
            index => step.RunAsync(
                FanOutIds.Item("item", chunk.Offset + index),
                itemToken => ProcessAsync(items[index], itemToken)),
            cancellationToken).ConfigureAwait(true);

        // A fail-fast chunk may end with items that never settled; the caller needs only the failure then.
        var outcomes = new List<FanOutOutcome<TResult>>(items.Count);
        for (var i = 0; i < items.Count; i++)
        {
            if (run.Failures[i] is { } failure)
            {
                outcomes.Add(new FanOutOutcome<TResult>(chunk.Offset + i, default, failure.Error));
            }
            else if (run.Settled[i])
            {
                outcomes.Add(new FanOutOutcome<TResult>(chunk.Offset + i, run.Values[i], null));
            }
        }

        return outcomes;
    }
}
