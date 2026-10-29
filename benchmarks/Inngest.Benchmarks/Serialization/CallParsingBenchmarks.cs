using System.Text.Json;
using BenchmarkDotNet.Attributes;
using Inngest.Benchmarks.Support;
using Inngest.Execution;

namespace Inngest.Benchmarks.Serialization;

// What the engine does with the body of a call before it runs any user code.
[MemoryDiagnoser]
public class CallParsingBenchmarks
{
    private ReadOnlyMemory<byte> _body;

    [Params(10, 100)]
    public int Steps { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _body = CallBody.Sequential(Samples.StepIds(Steps));

        using var document = JsonDocument.Parse(_body);
        var run = RunRequest.Parse(document.RootElement);
        Expect.Equal(Steps, run.Memo.Count, "the number of memoized steps");
        Expect.Equal(Steps, run.Order.Count, "the number of results to hand back");
        Expect.Equal(CallBody.RunId, run.RunId, "the run id");
        Expect.Equal(Samples.EventName, run.EventName, "the event name");
        Expect.Equal(1, run.Events.Count, "the number of events");
    }

    [Benchmark]
    public int ParseCall()
    {
        using var document = JsonDocument.Parse(_body);
        return RunRequest.Parse(document.RootElement).Memo.Count;
    }
}
