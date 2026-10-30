// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using Inngest.Steps;

namespace Inngest;

/// <summary>
/// Fan-out over a list as steps: one step per item with a stable id, results in the order of the items, and a limit
/// on how many items run at the same time. A list too long for one run goes to child runs in chunks instead.
/// </summary>
/// <remarks>
/// The list has to be the same on every replay, so take it from the event or from the result of a step. The ids of
/// the steps come from the position of the items, "id[0]", "id[1]" and so on, so a list that changes between calls
/// gives an item the result of another. Give each fan-out of a function an id of its own.
/// </remarks>
public static class FanOutExtensions
{
    // Inngest allows this many steps in a run, those of parallel groups included:
    // https://www.inngest.com/docs/durable-execution/limits
    private const int StepsPerRun = 1_000;

    /// <summary>
    /// Runs the handler for each item as a step with the id "id[index]" and returns the results in the order of the
    /// items. Up to <see cref="FanOutOptions.MaxParallelism"/> items run at the same time.
    /// </summary>
    /// <remarks>
    /// When an item's step fails for good, no more items start and the call throws that item's StepFailedException
    /// without waiting for the items in flight, which the SDK cannot recall. If several failed, it throws the one
    /// Inngest recorded first. When the token is canceled while items are left, the next one does not start and the
    /// call throws OperationCanceledException. A list of more than 1,000 items is refused, since a run has at most
    /// that many steps; MapInChunksAsync takes longer lists.
    /// </remarks>
    public static Task<IReadOnlyList<TResult>> MapAsync<TItem, TResult>(
        this IStepTools step,
        string id,
        IReadOnlyList<TItem> items,
        Func<TItem, CancellationToken, Task<TResult>> handler,
        FanOutOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var width = CheckInline(step, id, items, handler, options);
        return items.Count == 0
            ? Task.FromResult<IReadOnlyList<TResult>>([])
            : MapCoreAsync(step, id, items, handler, width, options, cancellationToken);
    }

    /// <summary>
    /// Runs the handler for each item as a step with the id "id[index]", like MapAsync but for a handler without a
    /// result. A failed item stops the list in the same way.
    /// </summary>
    public static Task ForEachAsync<TItem>(
        this IStepTools step,
        string id,
        IReadOnlyList<TItem> items,
        Func<TItem, CancellationToken, Task> handler,
        FanOutOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return step.MapAsync<TItem, object?>(
            id,
            items,
            async (item, itemToken) =>
            {
                await handler(item, itemToken).ConfigureAwait(false);
                return null;
            },
            options,
            cancellationToken);
    }

    /// <summary>
    /// Runs the handler for each item as a step with the id "id[index]", like MapAsync, and returns the outcome of
    /// every item in the order of the items instead of throwing when one fails. An item whose step failed for good has
    /// its error in its outcome, and the other items still run.
    /// </summary>
    public static Task<IReadOnlyList<FanOutOutcome<TResult>>> MapOutcomesAsync<TItem, TResult>(
        this IStepTools step,
        string id,
        IReadOnlyList<TItem> items,
        Func<TItem, CancellationToken, Task<TResult>> handler,
        FanOutOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var width = CheckInline(step, id, items, handler, options);
        return items.Count == 0
            ? Task.FromResult<IReadOnlyList<FanOutOutcome<TResult>>>([])
            : MapOutcomesCoreAsync(step, id, items, handler, width, options, cancellationToken);
    }

    /// <summary>
    /// Runs a list in chunks of chunkSize items, each chunk in a run of its own of the function TFunction, and returns
    /// the results in the order of the items. For lists that do not fit in one run, which has at most 1,000 steps.
    /// </summary>
    /// <remarks>
    /// The parent's run has an invoke step per chunk, with the id "id[first-last]". Up to
    /// <see cref="FanOutOptions.MaxParallelism"/> chunks run at the same time; the function decides how many items of a
    /// chunk do. When an item fails for good, its chunk starts no more items, no more chunks start, and the call throws
    /// a StepFailedException for that item, whose StepId is "id[index]". A chunk whose run fails or times out throws a
    /// StepFailedException for the chunk's step. A chunk is the data of an event and all the results end up in the
    /// state of the parent's run, so choose chunkSize with the limits of Inngest in mind.
    /// </remarks>
    public static Task<IReadOnlyList<TResult>> MapInChunksAsync<TFunction, TItem, TResult>(
        this IStepTools step,
        string id,
        IReadOnlyList<TItem> items,
        int chunkSize,
        FanOutOptions? options = null,
        CancellationToken cancellationToken = default)
        where TFunction : FanOutFunction<TItem, TResult>
    {
        var chunking = CheckChunked(step, id, items, chunkSize, options);
        return chunking.Count == 0
            ? Task.FromResult<IReadOnlyList<TResult>>([])
            : MapChunksCoreAsync<TFunction, TItem, TResult>(step, id, items, chunkSize, chunking, options, cancellationToken);
    }

    /// <summary>
    /// Runs a list in chunks like MapInChunksAsync, and returns the outcome of every item in the order of the items
    /// instead of throwing when one fails. Every item runs. When a chunk's run fails or times out, each of its items
    /// has the error of the chunk's step in its outcome.
    /// </summary>
    public static Task<IReadOnlyList<FanOutOutcome<TResult>>> MapOutcomesInChunksAsync<TFunction, TItem, TResult>(
        this IStepTools step,
        string id,
        IReadOnlyList<TItem> items,
        int chunkSize,
        FanOutOptions? options = null,
        CancellationToken cancellationToken = default)
        where TFunction : FanOutFunction<TItem, TResult>
    {
        var chunking = CheckChunked(step, id, items, chunkSize, options);
        return chunking.Count == 0
            ? Task.FromResult<IReadOnlyList<FanOutOutcome<TResult>>>([])
            : MapOutcomesChunksCoreAsync<TFunction, TItem, TResult>(step, id, items, chunkSize, chunking, options, cancellationToken);
    }

    // How many lanes run: the limit, or one per unit. Also the check of the limit.
    internal static int WidthOf(int? maxParallelism, int count, string name)
    {
        if (maxParallelism is { } max)
        {
            if (max < 1)
            {
                throw new ArgumentOutOfRangeException(name, max, "MaxParallelism must be at least 1.");
            }

            return Math.Min(max, count);
        }

        return count;
    }

    private static int CheckInline<TItem>(IStepTools step, string id, IReadOnlyList<TItem> items, Delegate handler, FanOutOptions? options)
    {
        ArgumentNullException.ThrowIfNull(step);
        ArgumentException.ThrowIfNullOrEmpty(id);
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(handler);
        if (items.Count > StepsPerRun)
        {
            throw new ArgumentException(
                $"The fan-out \"{id}\" has {items.Count} items and runs a step for each, but Inngest allows {StepsPerRun} steps in a run. Run the list in chunks with MapInChunksAsync.",
                nameof(items));
        }

        return WidthOf(options?.MaxParallelism, items.Count, nameof(options));
    }

    // How many chunks the list makes and how many of them run at once, after the checks of the arguments.
    private static Chunking CheckChunked<TItem>(IStepTools step, string id, IReadOnlyList<TItem> items, int chunkSize, FanOutOptions? options)
    {
        ArgumentNullException.ThrowIfNull(step);
        ArgumentException.ThrowIfNullOrEmpty(id);
        ArgumentNullException.ThrowIfNull(items);
        if (chunkSize < 1 || chunkSize > StepsPerRun)
        {
            throw new ArgumentOutOfRangeException(
                nameof(chunkSize),
                chunkSize,
                $"A chunk runs a step per item in a run of its own, so chunkSize must be between 1 and {StepsPerRun}, the steps Inngest allows in a run.");
        }

        var count = items.Count;
        var chunks = (count / chunkSize) + (count % chunkSize == 0 ? 0 : 1);
        if (chunks > StepsPerRun)
        {
            throw new ArgumentException(
                $"The fan-out \"{id}\" has {chunks} chunks of {chunkSize} items and runs an invoke step for each, but Inngest allows {StepsPerRun} steps in a run. Use a larger chunkSize, or split the list between runs.",
                nameof(items));
        }

        return new Chunking(chunks, WidthOf(options?.MaxParallelism, chunks, nameof(options)));
    }

    private static async Task<IReadOnlyList<TResult>> MapCoreAsync<TItem, TResult>(
        IStepTools step,
        string id,
        IReadOnlyList<TItem> items,
        Func<TItem, CancellationToken, Task<TResult>> handler,
        int width,
        FanOutOptions? options,
        CancellationToken cancellationToken)
    {
        var run = await FanOutRun<TResult>.RunAsync(
            items.Count,
            width,
            failFast: true,
            index => StartItem(step, id, index, items[index], handler, options),
            cancellationToken).ConfigureAwait(true);
        run.ThrowIfFailed();
        return run.Values;
    }

    private static async Task<IReadOnlyList<FanOutOutcome<TResult>>> MapOutcomesCoreAsync<TItem, TResult>(
        IStepTools step,
        string id,
        IReadOnlyList<TItem> items,
        Func<TItem, CancellationToken, Task<TResult>> handler,
        int width,
        FanOutOptions? options,
        CancellationToken cancellationToken)
    {
        var run = await FanOutRun<TResult>.RunAsync(
            items.Count,
            width,
            failFast: false,
            index => StartItem(step, id, index, items[index], handler, options),
            cancellationToken).ConfigureAwait(true);

        var outcomes = new FanOutOutcome<TResult>[items.Count];
        for (var i = 0; i < outcomes.Length; i++)
        {
            outcomes[i] = run.Failures[i] is { } failure
                ? new FanOutOutcome<TResult>(i, default, failure.Error)
                : new FanOutOutcome<TResult>(i, run.Values[i], null);
        }

        return outcomes;
    }

    private static Task<TResult> StartItem<TItem, TResult>(
        IStepTools step,
        string id,
        int index,
        TItem item,
        Func<TItem, CancellationToken, Task<TResult>> handler,
        FanOutOptions? options) =>
        step.RunAsync(
            new StepOptions(FanOutIds.Item(id, index)) { ParallelMode = options?.ParallelMode },
            cancellationToken => handler(item, cancellationToken));

    private static async Task<IReadOnlyList<TResult>> MapChunksCoreAsync<TFunction, TItem, TResult>(
        IStepTools step,
        string id,
        IReadOnlyList<TItem> items,
        int chunkSize,
        Chunking chunking,
        FanOutOptions? options,
        CancellationToken cancellationToken)
        where TFunction : FanOutFunction<TItem, TResult>
    {
        var outcomes = await ChunksAsync<TFunction, TItem, TResult>(step, id, items, chunkSize, chunking, options, failFast: true, cancellationToken).ConfigureAwait(true);
        var results = new TResult[outcomes.Length];
        for (var i = 0; i < results.Length; i++)
        {
            results[i] = outcomes[i].Value!;
        }

        return results;
    }

    private static async Task<IReadOnlyList<FanOutOutcome<TResult>>> MapOutcomesChunksCoreAsync<TFunction, TItem, TResult>(
        IStepTools step,
        string id,
        IReadOnlyList<TItem> items,
        int chunkSize,
        Chunking chunking,
        FanOutOptions? options,
        CancellationToken cancellationToken)
        where TFunction : FanOutFunction<TItem, TResult> =>
        await ChunksAsync<TFunction, TItem, TResult>(step, id, items, chunkSize, chunking, options, failFast: false, cancellationToken).ConfigureAwait(true);

    private static async Task<FanOutOutcome<TResult>[]> ChunksAsync<TFunction, TItem, TResult>(
        IStepTools step,
        string id,
        IReadOnlyList<TItem> items,
        int chunkSize,
        Chunking chunking,
        FanOutOptions? options,
        bool failFast,
        CancellationToken cancellationToken)
        where TFunction : FanOutFunction<TItem, TResult>
    {
        var count = items.Count;
        var run = await FanOutRun<IReadOnlyList<FanOutOutcome<TResult>>>.RunAsync(
            chunking.Count,
            chunking.Width,
            failFast,
            chunk => StartChunk<TFunction, TItem, TResult>(step, id, items, chunk, chunkSize, options, failFast),
            cancellationToken).ConfigureAwait(true);
        if (failFast)
        {
            run.ThrowIfFailed();
        }

        var outcomes = new FanOutOutcome<TResult>[count];
        var filled = new bool[count];
        for (var chunk = 0; chunk < chunking.Count; chunk++)
        {
            var first = chunk * chunkSize;
            var take = Math.Min(chunkSize, count - first);
            if (run.Failures[chunk] is { } failure)
            {
                // The chunk's run failed or timed out, so nothing is known about its items.
                for (var i = first; i < first + take; i++)
                {
                    outcomes[i] = new FanOutOutcome<TResult>(i, default, failure.Error);
                }

                continue;
            }

            var returned = run.Values[chunk];
            if (returned is null || returned.Count != take)
            {
                throw new InvalidOperationException(
                    $"The chunk {FanOutIds.Chunk(id, first, first + take - 1)} has {take} items, but {typeof(TFunction).Name} returned {returned?.Count ?? 0} outcomes for it.");
            }

            foreach (var outcome in returned)
            {
                if (outcome.Index < first || outcome.Index >= first + take || filled[outcome.Index])
                {
                    throw new InvalidOperationException(
                        $"The chunk {FanOutIds.Chunk(id, first, first + take - 1)} has the items {first} to {first + take - 1}, but {typeof(TFunction).Name} returned an outcome for item {outcome.Index}.");
                }

                filled[outcome.Index] = true;
                outcomes[outcome.Index] = outcome;
            }
        }

        return outcomes;
    }

    private static Task<IReadOnlyList<FanOutOutcome<TResult>>> StartChunk<TFunction, TItem, TResult>(
        IStepTools step,
        string id,
        IReadOnlyList<TItem> items,
        int chunk,
        int chunkSize,
        FanOutOptions? options,
        bool failFast)
        where TFunction : FanOutFunction<TItem, TResult>
    {
        var first = chunk * chunkSize;
        var take = Math.Min(chunkSize, items.Count - first);
        var slice = new TItem[take];
        for (var i = 0; i < take; i++)
        {
            slice[i] = items[first + i];
        }

        var invoked = step.InvokeAsync<TFunction, IReadOnlyList<FanOutOutcome<TResult>>>(
            new StepOptions(FanOutIds.Chunk(id, first, first + take - 1)) { ParallelMode = options?.ParallelMode },
            new FanOutChunk<TItem>(first, slice, failFast),
            options?.ChunkTimeout);
        return failFast ? ThrowOnFailedItemAsync(id, invoked) : invoked;
    }

    // A fail-fast chunk returns the outcomes of the items that ran, with the failure that stopped it. The step that
    // failed ran in the chunk's run, so the exception names the item as a step of this run would.
    private static async Task<IReadOnlyList<FanOutOutcome<TResult>>> ThrowOnFailedItemAsync<TResult>(
        string id,
        Task<IReadOnlyList<FanOutOutcome<TResult>>> invoked)
    {
        var outcomes = await invoked.ConfigureAwait(true)
            ?? throw new InvalidOperationException($"The run of a chunk of \"{id}\" returned no outcomes.");
        foreach (var outcome in outcomes)
        {
            if (outcome.Error is { } error)
            {
                throw new StepFailedException(FanOutIds.Item(id, outcome.Index), error);
            }
        }

        return outcomes;
    }

    private readonly record struct Chunking(int Count, int Width);
}
