using BenchmarkDotNet.Attributes;
using Inngest.Benchmarks.Support;

namespace Inngest.Benchmarks.Memoization;

// The first call of a run: the function finds one new step and the SDK runs it right away instead of reporting it.
[MemoryDiagnoser]
public class StepExecutionBenchmarks
{
    private EngineHarness _harness = null!;

    // What the scopes around the call and the step handler cost.
    [Params(true, false)]
    public bool LogScopes { get; set; }

    [GlobalSetup]
    public async Task Setup()
    {
        var plan = StepPlan.Of(1);
        _harness = EngineHarness.For<SingleStepFunction>(plan, CallBody.First(), expectedStatus: 206, logScopes: LogScopes);

        var op = await _harness.ExpectOpAsync("StepRun", plan.Ids[0]);
        Expect.Equal(Samples.ResultJson(0), Expect.Property(op, "data").GetRawText(), "the step output");
    }

    [GlobalCleanup]
    public void Cleanup() => _harness.Dispose();

    [Benchmark]
    public ValueTask<int> ExecuteNewStep() => _harness.RunAsync();
}
