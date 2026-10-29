using System.Collections.Concurrent;
using Inngest.IntegrationTests.Evaluation.Functions;
using Inngest.IntegrationTests.Evaluation.Harness;
using Inngest.IntegrationTests.Harness;
using Microsoft.Extensions.DependencyInjection;

namespace Inngest.IntegrationTests.Evaluation;

// Hundreds of runs at once, in the isolated collection: their bursts of connections would starve the other servers'
// calls.
[Collection(IsolatedCollection.Name)]
[Trait("Category", "Evaluation")]
[Trait("Area", "Durability")]
public sealed class LoadScenarios(EvaluationEnvironment environment) : EvaluationScenario<LoadFixture>(environment)
{
    [Fact(Timeout = 120_000)]
    public async Task Two_hundred_runs_started_at_once_all_complete_with_their_own_results()
    {
        var ids = await SendManyAsync(Enumerable.Range(1, 200).Select(value => new EventPayload(LoadFunctions.Runs, new ValueEvent(TestId, value))));

        await WaitForObservationsAsync("done", count: 200, timeout: TimeSpan.FromSeconds(60));
        var runs = await WaitForAllAsync(ids);

        runs.Values.ShouldAllBe(run => run.Status == RunStatus.Completed);
        ids.Select((id, index) => runs[id].OutputAs<int>() == ((index + 1) * 2) + 1).ShouldAllBe(matches => matches);
        Observed.Values<int>("done").ShouldBe(Enumerable.Range(1, 200), ignoreOrder: true);

        // The runs really ran side by side: the server ran dozens of the 200 ms steps at the same time.
        Intervals.MaxOverlap(Observed.Values<Interval>("work")).ShouldBeGreaterThanOrEqualTo(20);
    }

    [Fact(Timeout = 120_000)]
    public async Task A_hundred_runs_sleeping_at_the_same_time_all_wake_up_after_their_sleep()
    {
        var ids = await SendManyAsync(Enumerable.Range(1, 100).Select(value => new EventPayload(LoadFunctions.Sleepers, new ValueEvent(TestId, value))));

        var slept = await WaitForObservationsAsync("slept", count: 100, timeout: TimeSpan.FromSeconds(60));
        var runs = await WaitForAllAsync(ids);

        runs.Values.ShouldAllBe(run => run.Status == RunStatus.Completed);
        slept.Select(observation => ((Interval)observation.Value!).Duration).ShouldAllBe(duration => duration > LoadFunctions.Nap - TimeSpan.FromMilliseconds(300));
    }

    // Polls the runs a few at a time, which keeps the load on the server's API small next to the runs themselves.
    private async Task<IReadOnlyDictionary<string, RunInfo>> WaitForAllAsync(IReadOnlyList<string> ids)
    {
        var runs = new ConcurrentDictionary<string, RunInfo>(StringComparer.Ordinal);
        await Parallel.ForEachAsync(
            ids,
            new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = Cancellation },
            async (id, _) => runs[id] = await WaitForEndAsync(id, TimeSpan.FromSeconds(60)));
        return runs;
    }
}

// Runs too large for a single call, and a very long one, on the load server.
[Trait("Category", "Evaluation")]
[Trait("Area", "Durability")]
public sealed class LargeStateScenarios(EvaluationEnvironment environment) : EvaluationScenario<LoadFixture>(environment)
{
    private OutgoingRequests Outgoing => Fixture.App.Services.GetRequiredService<OutgoingRequests>();

    // Past 4 MB of state Inngest sends calls with use_api set and no steps, and the SDK loads them from the API.
    [Fact(Timeout = 120_000)]
    public async Task A_run_whose_state_passes_4_MB_goes_on_with_its_state_loaded_through_the_api()
    {
        var run = await RunToEndAsync(LargeStateFunctions.BigState, new RunEvent(TestId), TimeSpan.FromSeconds(60));

        run.Status.ShouldBe(RunStatus.Completed);
        var result = run.OutputAs<BigStateResult>();
        result.Lengths.ShouldBe([LargeStateFunctions.ChunkLength, LargeStateFunctions.ChunkLength, LargeStateFunctions.ChunkLength]);
        result.Hashes.ShouldBe([.. Enumerable.Range(1, 3).Select(index => StepDataFunctions.Sha256(StepDataFunctions.LargeText($"{TestId}-{index}", LargeStateFunctions.ChunkLength)))]);
        Observed.Values<int>("chunk").ShouldBe([1, 2, 3]);
        Outgoing.Snapshot().ShouldContain(request => request.PathAndQuery == $"/v0/runs/{run.RunId}/actions" && request.StatusCode == 200);
    }

    [Fact(Timeout = 120_000)]
    public async Task Parallel_steps_after_the_state_passes_4_MB_each_run_once_in_calls_that_load_the_state()
    {
        var run = await RunToEndAsync(LargeStateFunctions.BigParallel, new RunEvent(TestId), TimeSpan.FromSeconds(60));

        run.Status.ShouldBe(RunStatus.Completed);
        run.Output.ShouldNotBeNull().ShouldContainJson($$"""{ "lengths": {{3 * LargeStateFunctions.ChunkLength}}, "parts": [10, 20, 30] }""");
        Observed.Values<int>("part").ShouldBe([1, 2, 3], ignoreOrder: true);

        // One call for each parallel step and one that returns, at least, each loading the steps.
        Outgoing.Snapshot().Count(request => request.PathAndQuery == $"/v0/runs/{run.RunId}/actions" && request.StatusCode == 200)
            .ShouldBeGreaterThanOrEqualTo(4);
    }

    [Fact(Timeout = 120_000)]
    public async Task A_run_with_three_hundred_steps_runs_every_step_once_and_in_order()
    {
        var run = await RunToEndAsync(LargeStateFunctions.ManySteps, new ValueEvent(TestId, 300), TimeSpan.FromSeconds(100));

        run.Status.ShouldBe(RunStatus.Completed);
        run.OutputAs<ChainResult>().ShouldBe(new ChainResult(300, 300 * 301 / 2));
        Observed.Values<int>("step").ShouldBe(Enumerable.Range(1, 300));
    }

    // Ten events of 450 KB make a batch of 4.5 MB, which Inngest leaves out of the call for the SDK to load.
    [Fact(Timeout = 120_000)]
    public async Task A_batch_of_events_larger_than_4_MB_is_loaded_through_the_api()
    {
        const int BlobLength = 450_000;
        var blobs = Enumerable.Range(0, 10).Select(index => StepDataFunctions.LargeText($"{TestId}-blob-{index}", BlobLength)).ToArray();

        var ids = await Task.WhenAll(blobs.Select((blob, index) => SendAsync(LargeStateFunctions.BigBatch, new BlobEvent(TestId, index, blob))));
        await WaitForObservationsAsync("batch", timeout: TimeSpan.FromSeconds(30));
        var run = await Poll.UntilAsync(
            "the batch run to end",
            async _ => (await RunsOfAsync(ids)).FirstOrDefault(),
            found => found is { HasEnded: true },
            TimeSpan.FromSeconds(30),
            Fixture.DescribeAsync,
            Cancellation);

        run!.Status.ShouldBe(RunStatus.Completed);
        run.OutputAs<BatchSummary>().ShouldBe(
            new BatchSummary(10, [.. Enumerable.Range(0, 10)], 10L * BlobLength, StepDataFunctions.Sha256(string.Concat(blobs))),
            new BatchSummaryComparer());
        Outgoing.Snapshot().ShouldContain(request => request.PathAndQuery == $"/v0/runs/{run.RunId}/batch" && request.StatusCode == 200);
    }

    private sealed class BatchSummaryComparer : IEqualityComparer<BatchSummary>
    {
        public bool Equals(BatchSummary? x, BatchSummary? y) =>
            x is not null && y is not null && x.Count == y.Count && x.TotalLength == y.TotalLength && x.Sha256 == y.Sha256 && x.Indexes.SequenceEqual(y.Indexes);

        public int GetHashCode(BatchSummary obj) => obj.Count;
    }
}
