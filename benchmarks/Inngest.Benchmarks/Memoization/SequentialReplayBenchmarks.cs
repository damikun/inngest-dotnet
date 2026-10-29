using BenchmarkDotNet.Attributes;
using Inngest.Benchmarks.Support;

namespace Inngest.Benchmarks.Memoization;

// A call that finds every step memoized: the function runs from the top, gets each result back and returns.
[MemoryDiagnoser]
public class SequentialReplayBenchmarks
{
    private EngineHarness _harness = null!;

    [Params(1, 10, 100, 500)]
    public int Steps { get; set; }

    [GlobalSetup]
    public async Task Setup()
    {
        _harness = EngineHarness.For<SequentialFunction>(StepPlan.Of(Steps), CallBody.Sequential(Samples.StepIds(Steps)), expectedStatus: 200);
        await _harness.ExpectSummaryAsync(Steps);
    }

    [GlobalCleanup]
    public void Cleanup() => _harness.Dispose();

    [Benchmark]
    public ValueTask<int> ReplaySequentialSteps() => _harness.RunAsync();
}
