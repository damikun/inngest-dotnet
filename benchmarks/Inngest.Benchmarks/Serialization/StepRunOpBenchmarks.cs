using System.Text.Json;
using BenchmarkDotNet.Attributes;
using Inngest.Benchmarks.Support;
using Inngest.Execution;
using Inngest.Internal;
using Inngest.Steps;

namespace Inngest.Benchmarks.Serialization;

// The answer to a call that ran one step: its output as a record, in a StepRun op. The engine writes the op in a
// private method, so this repeats its writer calls, and setup compares the result with the engine's answer.
[MemoryDiagnoser]
public class StepRunOpBenchmarks
{
    private const long StartNanoseconds = 1793268000000000000;
    private const long DurationNanoseconds = 1_500_000;

    private static readonly JsonEncodedText DataProperty = JsonEncodedText.Encode("data");
    private static readonly JsonEncodedText TimingProperty = JsonEncodedText.Encode("timing");
    private static readonly JsonEncodedText StartProperty = JsonEncodedText.Encode("a");
    private static readonly JsonEncodedText DurationProperty = JsonEncodedText.Encode("b");

    private InngestSettings _settings = null!;
    private RunStep<StepResult> _step = null!;
    private StepResult _output = null!;

    [GlobalSetup]
    public async Task Setup()
    {
        var id = Samples.StepIds(1)[0];
        _settings = InngestSettings.Create(new InngestOptions { AppId = "shop", IsDev = true }, static _ => null);
        _output = Samples.Result(0);
        _step = new RunStep<StepResult>(id, StepHandler.Charge);
        _step.Attach(id, index: null, StepIds.Hash(id), memo: null);

        using var harness = EngineHarness.For<SingleStepFunction>(StepPlan.Of(1), CallBody.First(), expectedStatus: 206);
        var expected = await harness.ExpectOpAsync("StepRun", id);
        using var body = new PooledBufferWriter();
        WriteOp(body);
        using var written = JsonDocument.Parse(body.WrittenMemory);

        var actual = written.RootElement[0];
        Expect.Equal("StepRun", Expect.Property(actual, "op").GetString(), "the op");
        Expect.Equal(Samples.ResultJson(0), Expect.Property(actual, "data").GetRawText(), "the step output");
        foreach (var property in expected.EnumerateObject().Where(static property => property.Name != "timing"))
        {
            Expect.Equal(property.Value.GetRawText(), Expect.Property(actual, property.Name).GetRawText(), $"the \"{property.Name}\" of the op");
        }

        Expect.Equal(expected.EnumerateObject().Count(), actual.EnumerateObject().Count(), "the number of properties of the op");
    }

    [Benchmark]
    public int WriteStepRunOp()
    {
        using var body = new PooledBufferWriter();
        WriteOp(body);
        return body.WrittenCount;
    }

    private void WriteOp(PooledBufferWriter body)
    {
        using var writer = WireWriter.Create(body, _settings.SerializerOptions);
        writer.WriteStartArray();
        _step.WriteOpStart(writer, Ops.StepRun, _settings);
        writer.WritePropertyName(DataProperty);
        _step.WriteOutput(writer, _output, _settings.SerializerOptions);
        writer.WriteStartObject(TimingProperty);
        writer.WriteNumber(StartProperty, StartNanoseconds);
        writer.WriteNumber(DurationProperty, DurationNanoseconds);
        writer.WriteEndObject();
        writer.WriteEndObject();
        writer.WriteEndArray();
    }
}
