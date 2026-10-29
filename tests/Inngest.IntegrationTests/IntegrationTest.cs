using Inngest.IntegrationTests.Harness;

namespace Inngest.IntegrationTests;

// Base for tests that drive the SDK through a real Inngest server. Every test has its own id, which goes into the data
// of each event it sends; the functions record what they observe under it. When a test fails, the server's log, the
// sync state and what reached the app are written to its output. A failed sync carries the same in its message.
public abstract class IntegrationTest<TFixture>(TFixture fixture) : IAsyncLifetime
    where TFixture : InngestFixture
{
    protected TFixture Fixture { get; } = fixture;

    protected DevServerClient Api => Fixture.Api;

    protected string TestId { get; } = NewTestId();

    // What the test functions recorded for this test.
    protected TestRecording Observed => Fixture.App.Probe.For(TestId);

    protected static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    public virtual async ValueTask InitializeAsync() => await Fixture.EnsureSyncedAsync(Cancellation);

    public async ValueTask DisposeAsync()
    {
        if (TestContext.Current.TestState?.Result == TestResult.Failed && TestContext.Current.TestOutputHelper is { } output)
        {
            output.WriteLine($"Test id {TestId}; the test functions recorded {Observed}");
            output.WriteLine(await Fixture.DescribeAsync(CancellationToken.None));
        }

        GC.SuppressFinalize(this);
    }

    protected static string NewTestId() => Guid.NewGuid().ToString("N");

    // Sends the event and waits until the one run it starts has ended.
    protected async Task<RunInfo> RunToEndAsync(string eventName, object data)
    {
        var eventId = await Api.SendEventAsync(eventName, data, Cancellation);
        return await Api.WaitForRunEndAsync(eventId, Cancellation);
    }
}
