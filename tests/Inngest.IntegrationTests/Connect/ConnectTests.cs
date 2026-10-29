using Inngest.Internal;
using Inngest.IntegrationTests.Functions;
using Inngest.IntegrationTests.Harness;

namespace Inngest.IntegrationTests.Connect;

// The app runs as a Connect worker with no HTTP endpoint: the dev server syncs it through the gateway and makes every
// call over the worker's WebSocket.
[Collection(ConnectCollection.Name)]
public sealed class ConnectTests(ConnectFixture fixture) : IAsyncLifetime
{
    private readonly string _testId = Guid.NewGuid().ToString("N");

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    private TestRecording Observed => fixture.Worker.Probe.For(_testId);

    public async ValueTask InitializeAsync() => await fixture.EnsureSyncedAsync(Cancellation);

    public async ValueTask DisposeAsync()
    {
        if (TestContext.Current.TestState?.Result == TestResult.Failed && TestContext.Current.TestOutputHelper is { } output)
        {
            output.WriteLine($"Test id {_testId}; the functions recorded {Observed}");
            output.WriteLine(await fixture.DescribeAsync(CancellationToken.None));
        }
    }

    [Fact(Timeout = 60_000)]
    public async Task A_worker_without_an_http_endpoint_syncs_its_functions()
    {
        var functions = await fixture.Api.GetAppFunctionsAsync(ConnectFunctions.AppId, Cancellation);

        functions.Select(function => function.Slug).ShouldBe(ConnectFunctions.Slugs, ignoreOrder: true);
        functions.ShouldAllBe(function => function.StepUrl.StartsWith("ws", StringComparison.Ordinal));
        var app = (await fixture.Api.GetAppsAsync(Cancellation)).Single(app => app.ExternalId == ConnectFunctions.AppId);
        app.Error.ShouldBeNull();
        app.SdkLanguage.ShouldBe(SdkInfo.Language);
        app.SdkVersion.ShouldBe($"v{SdkInfo.Version}");

        var connection = (await fixture.GetConnectionsAsync(fixture.Worker.InstanceId, Cancellation)).First();
        connection.Status.ShouldBe("READY");
        connection.AppName.ShouldBe(ConnectFunctions.AppId);
        connection.SdkLanguage.ShouldBe(SdkInfo.Language);
    }

    [Fact(Timeout = 60_000)]
    public async Task Runs_steps_parallel_steps_and_a_sleep_to_completion()
    {
        var eventId = await fixture.Api.SendEventAsync("itests/connect.steps", new NumberEvent(_testId, 2), Cancellation);

        var run = await fixture.Api.WaitForRunEndAsync(eventId, Cancellation, TimeSpan.FromSeconds(30));

        run.Status.ShouldBe(RunStatus.Completed);
        run.OutputAs<ConnectStepsResult>().ShouldBe(new ConnectStepsResult(3, [3, 6, 9], 18), new ConnectStepsResultComparer());
        Observed.Names.ShouldBe(["start", "part-1", "part-2", "part-3", "total"], ignoreOrder: true);
        var sleptFor = Observed.Named("total")[0].At - Observed.Named("part-1").Concat(Observed.Named("part-2")).Concat(Observed.Named("part-3")).Max(observation => observation.At);
        sleptFor.ShouldBeGreaterThanOrEqualTo(ConnectStepsFunction.Pause - TimeSpan.FromMilliseconds(50));
    }

    [Fact(Timeout = 60_000)]
    public async Task Retries_a_step_that_fails()
    {
        var eventId = await fixture.Api.SendEventAsync("itests/connect.retry", new TestEvent(_testId), Cancellation);

        var run = await fixture.Api.WaitForRunEndAsync(eventId, Cancellation, TimeSpan.FromSeconds(30));

        run.Status.ShouldBe(RunStatus.Completed);
        run.OutputAs<TextResult>().ShouldBe(new TextResult("recovered"));
        Observed.Values<int>("flaky").ShouldBe([0, 1]);
    }

    private sealed class ConnectStepsResultComparer : IEqualityComparer<ConnectStepsResult>
    {
        public bool Equals(ConnectStepsResult? x, ConnectStepsResult? y) =>
            x is not null && y is not null && x.Start == y.Start && x.Total == y.Total && x.Parts.SequenceEqual(y.Parts);

        public int GetHashCode(ConnectStepsResult obj) => obj.Total;
    }
}
