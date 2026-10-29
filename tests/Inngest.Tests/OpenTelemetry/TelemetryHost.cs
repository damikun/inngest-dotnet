using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text;
using Inngest.Execution;
using Inngest.Functions;
using Inngest.Tests.Execution;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Inngest.Tests.OpenTelemetry;

// The real engine with AddInstrumentation, called the way the hosts call it: one DI scope per call. The listeners keep
// what belongs to this host: spans of its run id or of its own test source, and measurements of its container's meter.
internal sealed class TelemetryHost : IAsyncDisposable
{
    private readonly ServiceProvider _provider;
    private readonly ActivityListener _spanListener;
    private readonly MeterListener _meterListener;
    private readonly ConcurrentQueue<Activity> _spans = new();
    private readonly ConcurrentQueue<Measured> _measurements = new();

    public TelemetryHost(Action<IServiceCollection>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddFakeLogging();
        services.AddMetrics();
        services.AddInngest(options =>
        {
            options.AppId = "shop";
            options.IsDev = true;
        }).AddInstrumentation();
        configure?.Invoke(services);
        _provider = services.BuildServiceProvider();
        Engine = _provider.GetRequiredService<IExecutionEngine>();

        _spanListener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == InngestInstrumentation.ActivitySourceName || source == TestSource,
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                if (activity.Source == TestSource || Equals(activity.GetTagItem("sdk.run.id"), RunId))
                {
                    _spans.Enqueue(activity);
                }
            },
        };
        ActivitySource.AddActivityListener(_spanListener);

        var meters = _provider.GetRequiredService<IMeterFactory>();
        _meterListener = new MeterListener
        {
            InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Scope == meters)
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            },
        };
        _meterListener.SetMeasurementEventCallback<long>((instrument, value, tags, _) => _measurements.Enqueue(new Measured(instrument.Name, value, Tags(tags))));
        _meterListener.SetMeasurementEventCallback<double>((instrument, value, tags, _) => _measurements.Enqueue(new Measured(instrument.Name, value, Tags(tags))));
        _meterListener.Start();
    }

    // Unique per host, so the spans of hosts in other tests stay out.
    public string RunId { get; } = $"01JRUN{Guid.NewGuid():N}";

    // Stands in for the app's own instrumentation, such as an HTTP client inside a step.
    public ActivitySource TestSource { get; } = new($"Inngest.Tests.{Guid.NewGuid():N}");

    public IExecutionEngine Engine { get; }

    public IReadOnlyList<Activity> Spans => [.. _spans];

    public IReadOnlyList<Measured> Measurements => [.. _measurements];

    public Activity Span(string name) => Spans.Where(span => span.OperationName == name).ShouldHaveSingleItem();

    public IReadOnlyList<Measured> Measured(string instrument) => [.. Measurements.Where(measured => measured.Instrument == instrument)];

    // queueItemId adds the ctx.qi_id that Inngest sends with every call, which checkpointing needs.
    public async Task<int> CallAsync(FunctionDefinition function, Call call, string? stepId = null, string? queueItemId = null)
    {
        var body = Encoding.UTF8.GetString(call.ToBytes()).Replace("\"run_id\":\"01JRUN\"", $"\"run_id\":\"{RunId}\"", StringComparison.Ordinal);
        if (queueItemId is not null)
        {
            body = body.Replace("\"ctx\":{", $"\"ctx\":{{\"qi_id\":\"{queueItemId}\",", StringComparison.Ordinal);
        }

        var scope = _provider.CreateAsyncScope();
        await using (scope)
        {
            using var result = await Engine.ExecuteAsync(
                new ExecutionRequest
                {
                    Function = function,
                    Body = Encoding.UTF8.GetBytes(body),
                    StepId = stepId ?? "step",
                    Services = scope.ServiceProvider,
                },
                TestContext.Current.CancellationToken);
            return result.StatusCode;
        }
    }

    public async ValueTask DisposeAsync()
    {
        _spanListener.Dispose();
        _meterListener.Dispose();
        TestSource.Dispose();
        await _provider.DisposeAsync();
    }

    private static Dictionary<string, object?> Tags(ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        var copy = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var tag in tags)
        {
            copy[tag.Key] = tag.Value;
        }

        return copy;
    }
}

internal sealed record Measured(string Instrument, double Value, IReadOnlyDictionary<string, object?> Tags);
