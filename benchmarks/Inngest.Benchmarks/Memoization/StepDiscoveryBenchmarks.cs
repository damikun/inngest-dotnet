using BenchmarkDotNet.Attributes;
using Inngest.Benchmarks.Support;

namespace Inngest.Benchmarks.Memoization;

// A call that replays memoized steps and then reaches a sleep, which the SDK reports to Inngest in a 206 instead
// of running it.
[MemoryDiagnoser]
public class StepDiscoveryBenchmarks
{
    private EngineHarness _harness = null!;

    [Params(10, 100)]
    public int Steps { get; set; }

    [GlobalSetup]
    public async Task Setup()
    {
        _harness = EngineHarness.For<SequentialThenSleepFunction>(StepPlan.Of(Steps), CallBody.Sequential(Samples.StepIds(Steps)), expectedStatus: 206);

        var op = await _harness.ExpectOpAsync("Sleep", StepPlan.SleepId);
        Expect.Equal("2d", Expect.Property(Expect.Property(op, "opts"), "duration").GetString(), "the duration of the sleep");
    }

    [GlobalCleanup]
    public void Cleanup() => _harness.Dispose();

    [Benchmark]
    public ValueTask<int> DiscoverStep() => _harness.RunAsync();
}
