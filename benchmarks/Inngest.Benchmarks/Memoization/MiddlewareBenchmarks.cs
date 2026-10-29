using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using Inngest.Benchmarks.Support;

namespace Inngest.Benchmarks.Memoization;

// What one middleware that overrides nothing costs. Every hook still runs for it, and WrapStepAsync wraps every
// step, memoized or not.
[MemoryDiagnoser]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[CategoriesColumn]
public class MiddlewareBenchmarks
{
    private const int Steps = 100;
    private const string Replay = "Replay 100 steps";
    private const string Execute = "Execute a new step";

    private EngineHarness _replay = null!;
    private EngineHarness _replayWithMiddleware = null!;
    private EngineHarness _execute = null!;
    private EngineHarness _executeWithMiddleware = null!;

    [GlobalSetup]
    public async Task Setup()
    {
        var ids = StepPlan.Of(Steps);
        var replayBody = CallBody.Sequential(ids.Ids);
        _replay = EngineHarness.For<SequentialFunction>(ids, replayBody, expectedStatus: 200);
        _replayWithMiddleware = EngineHarness.For<SequentialFunction>(ids, replayBody, expectedStatus: 200, noOpMiddleware: true);

        var one = StepPlan.Of(1);
        _execute = EngineHarness.For<SingleStepFunction>(one, CallBody.First(), expectedStatus: 206);
        _executeWithMiddleware = EngineHarness.For<SingleStepFunction>(one, CallBody.First(), expectedStatus: 206, noOpMiddleware: true);

        Expect.Equal(0, _replay.MiddlewareCount, "the number of middleware without the no-op middleware");
        Expect.Equal(1, _replayWithMiddleware.MiddlewareCount, "the number of middleware with the no-op middleware");
        Expect.Equal(0, _execute.MiddlewareCount, "the number of middleware without the no-op middleware");
        Expect.Equal(1, _executeWithMiddleware.MiddlewareCount, "the number of middleware with the no-op middleware");

        await _replay.ExpectSummaryAsync(Steps);
        await _replayWithMiddleware.ExpectSummaryAsync(Steps);
        await _execute.ExpectOpAsync("StepRun", one.Ids[0]);
        await _executeWithMiddleware.ExpectOpAsync("StepRun", one.Ids[0]);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _replay.Dispose();
        _replayWithMiddleware.Dispose();
        _execute.Dispose();
        _executeWithMiddleware.Dispose();
    }

    [Benchmark(Baseline = true), BenchmarkCategory(Replay)]
    public ValueTask<int> ReplayWithoutMiddleware() => _replay.RunAsync();

    [Benchmark, BenchmarkCategory(Replay)]
    public ValueTask<int> ReplayWithMiddleware() => _replayWithMiddleware.RunAsync();

    [Benchmark(Baseline = true), BenchmarkCategory(Execute)]
    public ValueTask<int> ExecuteWithoutMiddleware() => _execute.RunAsync();

    [Benchmark, BenchmarkCategory(Execute)]
    public ValueTask<int> ExecuteWithMiddleware() => _executeWithMiddleware.RunAsync();
}
