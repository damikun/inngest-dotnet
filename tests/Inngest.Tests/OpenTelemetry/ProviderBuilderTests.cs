using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace Inngest.Tests.OpenTelemetry;

public sealed class ProviderBuilderTests
{
    [Fact]
    public void Tracing_collects_the_inngest_source()
    {
        var builder = new RecordingTracerProviderBuilder();

        builder.AddInngestInstrumentation().ShouldBeSameAs(builder);

        builder.Sources.ShouldBe([InngestInstrumentation.ActivitySourceName]);
        InngestInstrumentation.ActivitySourceName.ShouldBe("inngest");
    }

    [Fact]
    public void Metrics_collect_the_inngest_meter()
    {
        var builder = new RecordingMeterProviderBuilder();

        builder.AddInngestInstrumentation().ShouldBeSameAs(builder);

        builder.Meters.ShouldBe([InngestInstrumentation.MeterName]);
        InngestInstrumentation.MeterName.ShouldBe("inngest");
    }

    private sealed class RecordingTracerProviderBuilder : TracerProviderBuilder
    {
        public List<string> Sources { get; } = [];

        public override TracerProviderBuilder AddInstrumentation<TInstrumentation>(Func<TInstrumentation> instrumentationFactory) => this;

        public override TracerProviderBuilder AddSource(params string[] names)
        {
            Sources.AddRange(names);
            return this;
        }

        public override TracerProviderBuilder AddLegacySource(string operationName) => this;
    }

    private sealed class RecordingMeterProviderBuilder : MeterProviderBuilder
    {
        public List<string> Meters { get; } = [];

        public override MeterProviderBuilder AddInstrumentation<TInstrumentation>(Func<TInstrumentation> instrumentationFactory) => this;

        public override MeterProviderBuilder AddMeter(params string[] names)
        {
            Meters.AddRange(names);
            return this;
        }
    }
}
