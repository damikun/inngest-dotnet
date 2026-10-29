using BenchmarkDotNet.Attributes;
using Inngest.Benchmarks.Support;

namespace Inngest.Benchmarks.Memoization;

// A call that finds every step memoized, for steps started together and awaited with Task.WhenAll. They completed in
// no particular order, so the engine hands the results back in an order other than the one the function asked for them in.
[MemoryDiagnoser]
public class ParallelReplayBenchmarks
{
    private EngineHarness _harness = null!;

    [Params(10, 100)]
    public int Steps { get; set; }

    [GlobalSetup]
    public async Task Setup()
    {
        _harness = EngineHarness.For<ParallelFunction>(StepPlan.Of(Steps), CallBody.Parallel(Samples.StepIds(Steps)), expectedStatus: 200);
        await _harness.ExpectSummaryAsync(Steps);
    }

    [GlobalCleanup]
    public void Cleanup() => _harness.Dispose();

    [Benchmark]
    public ValueTask<int> ReplayParallelSteps() => _harness.RunAsync();
}
