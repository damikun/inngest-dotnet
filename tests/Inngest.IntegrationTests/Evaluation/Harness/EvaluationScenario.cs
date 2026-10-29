using System.Diagnostics;
using System.Text.Json;
using Inngest.IntegrationTests.Harness;

namespace Inngest.IntegrationTests.Evaluation.Harness;

// Base of every evaluation scenario. Like IntegrationTest, each scenario has its own test id, which goes into the data
// of every event it sends and keys what the functions record; a failing scenario writes what the functions recorded,
// the sync state and the server's log to its output.
public abstract class EvaluationScenario<TFixture>(EvaluationEnvironment environment) : IAsyncLifetime
    where TFixture : class, IEvaluationFixture, new()
{
    // How long a scenario watches for a run that must not start. Events start their runs within about 0.3 s.
    protected static readonly TimeSpan NoRunWindow = TimeSpan.FromSeconds(1.5);

    protected TFixture Fixture { get; private set; } = null!;

    protected DevServerClient Api => Fixture.Api;

    protected string TestId { get; } = Guid.NewGuid().ToString("N");

    protected TestRecording Observed => Fixture.Probe.For(TestId);

    protected static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    public virtual async ValueTask InitializeAsync()
    {
        Fixture = await environment.GetAsync<TFixture>();
        await Fixture.EnsureSyncedAsync(Cancellation);
    }

    public virtual async ValueTask DisposeAsync()
    {
        if (Fixture is not null
            && TestContext.Current.TestState?.Result == TestResult.Failed
            && TestContext.Current.TestOutputHelper is { } output)
        {
            output.WriteLine($"Test id {TestId}; the functions recorded {Observed}");
            output.WriteLine(await Fixture.DescribeAsync(CancellationToken.None));
        }

        GC.SuppressFinalize(this);
    }

    protected Task<string> SendAsync(string name, object data) => Api.SendEventAsync(name, data, Cancellation);

    protected Task<IReadOnlyList<string>> SendManyAsync(IEnumerable<EventPayload> events) =>
        Api.SendEventsAsync([.. events], Cancellation);

    protected Task<RunInfo> WaitForRunAsync(string eventId, TimeSpan? timeout = null) =>
        Api.WaitForRunAsync(eventId, Cancellation, timeout);

    protected Task<RunInfo> WaitForEndAsync(string eventId, TimeSpan? timeout = null) =>
        Api.WaitForRunEndAsync(eventId, Cancellation, timeout);

    // Sends the event and waits until the one run it starts has ended.
    protected async Task<RunInfo> RunToEndAsync(string name, object data, TimeSpan? timeout = null) =>
        await WaitForEndAsync(await SendAsync(name, data), timeout);

    // Waits until the run is parked on a step of the given op, such as a sleep or a wait for an event.
    protected Task<RunTrace> WaitForStepAsync(string runId, string stepOp, int count = 1, TimeSpan? timeout = null) =>
        Api.WaitForTraceAsync(
            runId,
            $"{count} {stepOp} step(s) WAITING",
            trace => trace.Spans.Count(span => span.StepOp == stepOp && span.Status == SpanStatuses.Waiting) >= count,
            Cancellation,
            timeout);

    // Waits for every run the event starts to end, for events that trigger several functions.
    protected async Task<IReadOnlyList<RunInfo>> WaitForRunsToEndAsync(string eventId, int count, TimeSpan? timeout = null)
    {
        var runs = await Poll.UntilAsync(
            $"event {eventId} to start {count} runs that end",
            async token => await Api.GetEventRunsAsync(eventId, token),
            found => found.Count >= count && found.All(run => run.HasEnded),
            timeout ?? DevServerClient.DefaultTimeout,
            Fixture.DescribeAsync,
            Cancellation);

        runs.Count.ShouldBe(count, $"event {eventId} started {runs.Count} runs: {string.Join(", ", runs)}");
        return runs;
    }

    // The runs any of the events started, each once.
    protected async Task<IReadOnlyList<RunInfo>> RunsOfAsync(params string[] eventIds)
    {
        var runs = new Dictionary<string, RunInfo>(StringComparer.Ordinal);
        foreach (var eventId in eventIds)
        {
            foreach (var run in await Api.GetEventRunsAsync(eventId, Cancellation))
            {
                runs.TryAdd(run.RunId, run);
            }
        }

        return [.. runs.Values];
    }

    // The statuses of the event's runs as GraphQL lists them, which includes runs that flow control skipped (SKIPPED);
    // the REST API leaves those out.
    protected Task<IReadOnlyList<string>> WaitForRunStatusesAsync(string eventId) =>
        Poll.UntilAsync<IReadOnlyList<string>>(
            $"event {eventId} to list a run in GraphQL",
            async token =>
            {
                using var body = await Api.QueryAsync("query ($id: ULID!) { eventV2(id: $id) { runs { status } } }", new { id = eventId }, token);
                var runs = body.RootElement.GetProperty("data").GetProperty("eventV2").GetProperty("runs");
                return runs.ValueKind == JsonValueKind.Array ? [.. runs.EnumerateArray().Select(run => run.GetProperty("status").GetString()!)] : [];
            },
            statuses => statuses.Count > 0,
            DevServerClient.DefaultTimeout,
            Fixture.DescribeAsync,
            Cancellation);

    // Fails if any of the events starts a run within the window.
    protected async Task ShouldStartNoRunAsync(params string[] eventIds)
    {
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < NoRunWindow)
        {
            foreach (var eventId in eventIds)
            {
                var runs = await Api.GetEventRunsAsync(eventId, Cancellation);
                runs.ShouldBeEmpty($"event {eventId} must not start a run, but started {string.Join(", ", runs)}");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250), Cancellation);
        }
    }

    // Waits until the functions have recorded name at least count times for this test.
    protected Task<IReadOnlyList<Observation>> WaitForObservationsAsync(string name, int count = 1, TimeSpan? timeout = null) =>
        Poll.UntilAsync(
            $"the functions to record \"{name}\" {count} time(s)",
            _ => Observed.Named(name),
            named => named.Count >= count,
            timeout ?? DevServerClient.DefaultTimeout,
            Cancellation);
}
