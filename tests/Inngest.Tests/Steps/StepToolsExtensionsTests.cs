namespace Inngest.Tests.Steps;

public sealed class StepToolsExtensionsTests
{
    [Fact]
    public async Task A_handler_returning_a_ValueTask_is_rejected_instead_of_saved_unawaited()
    {
        var steps = new RecordingStepTools();

        var exception = await Should.ThrowAsync<ArgumentException>(() => steps.RunAsync("enqueue", () => ValueTask.CompletedTask));

        exception.Message.ShouldContain("\"enqueue\"");
        steps.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task A_synchronous_handler_returns_its_value()
    {
        var steps = new RecordingStepTools();

        (await steps.RunAsync("compute", () => 42)).ShouldBe(42);
        steps.Calls.ShouldBe(1);
    }

    private sealed class RecordingStepTools : IStepTools
    {
        public int Calls { get; private set; }

        public async Task<T> RunAsync<T>(string id, Func<CancellationToken, Task<T>> handler)
        {
            Calls++;
            return await handler(CancellationToken.None);
        }

        public Task SleepAsync(string id, TimeSpan duration) => throw new NotSupportedException();

        public Task SleepUntilAsync(string id, DateTimeOffset until) => throw new NotSupportedException();

        public Task<InngestEvent<TData>?> WaitForEventAsync<TData>(string id, WaitForEventOptions options) => throw new NotSupportedException();

        public Task<TResult> InvokeAsync<TResult>(string id, InvokeOptions options) => throw new NotSupportedException();

        public Task<SendEventsResult> SendEventAsync(string id, IReadOnlyList<InngestEvent> events) => throw new NotSupportedException();
    }
}
