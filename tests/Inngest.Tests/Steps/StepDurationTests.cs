using Inngest.Tests.Execution;

namespace Inngest.Tests.Steps;

// Sleeps and waits last whole seconds, and the ops say so. The first two cases are the TypeScript SDK's integration
// tests for sub-second durations, which sleep for one second at the least.
public sealed class StepDurationTests : IDisposable
{
    private readonly EngineHost _host = new();

    public void Dispose() => _host.Dispose();

    [Theory]
    [InlineData(500, "1s")]
    [InlineData(1, "1s")]
    [InlineData(1_500, "1s")]
    [InlineData(65_500, "1m5s")]
    public async Task A_sleep_is_reported_in_whole_seconds_and_never_rounds_down_to_nothing(int milliseconds, string expected)
    {
        var result = await Report(step => step.SleepAsync("nap", TimeSpan.FromMilliseconds(milliseconds)));

        var op = result.Op();
        op.GetProperty("op").GetString().ShouldBe("Sleep");
        op.GetProperty("name").GetString().ShouldBe(expected);
        op.GetProperty("opts").GetProperty("duration").GetString().ShouldBe(expected);
    }

    [Theory]
    [InlineData(250, "1s")]
    [InlineData(90_000, "1m30s")]
    public async Task A_wait_for_event_timeout_is_reported_in_whole_seconds(int milliseconds, string expected)
    {
        var result = await Report(step => step.WaitForEventAsync("wait", new WaitForEventOptions("app/paid", TimeSpan.FromMilliseconds(milliseconds))));

        result.Op().GetProperty("opts").GetProperty("timeout").GetString().ShouldBe(expected);
    }

    [Theory]
    [InlineData(2_000, "2s")]
    [InlineData(3_600_000, "1h")]
    [InlineData(100, "1s")]
    public async Task An_invoke_timeout_is_reported_in_whole_seconds(int milliseconds, string expected)
    {
        var result = await Report(step => step.InvokeAsync<int>("invoke", new InvokeOptions("child") { Timeout = TimeSpan.FromMilliseconds(milliseconds) }));

        result.Op().GetProperty("opts").GetProperty("timeout").GetString().ShouldBe(expected);
    }

    [Fact]
    public async Task A_wait_for_event_that_timed_out_returns_null_to_the_function()
    {
        var function = TestFunction.Define(async context =>
        {
            var found = await context.Step.WaitForEventAsync("wait", new WaitForEventOptions("app/paid", TimeSpan.FromMilliseconds(250)));
            return found is null ? "timed out" : "found";
        });

        var result = await _host.CallAsync(function, Call.New().Memo("wait", "null"));

        result.Body.ShouldBe("\"timed out\"");
    }

    [Fact]
    public async Task A_sleep_until_a_time_in_the_past_is_still_reported_as_a_sleep()
    {
        var result = await Report(step => step.SleepUntilAsync("late", DateTimeOffset.UnixEpoch));

        var op = result.Op();
        op.GetProperty("op").GetString().ShouldBe("Sleep");
        op.GetProperty("name").GetString().ShouldBe("1970-01-01T00:00:00.000Z");
    }

    private Task<CallResult> Report(Func<IStepTools, Task> step) =>
        _host.CallAsync(
            TestFunction.Define(async context =>
            {
                await step(context.Step);
                return null;
            }),
            Call.New());
}
