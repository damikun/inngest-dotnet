namespace Inngest.Tests.Client;

internal sealed class UnusedStepTools : IStepTools
{
    public Task<T> RunAsync<T>(string id, Func<CancellationToken, Task<T>> handler) => throw new NotSupportedException();

    public Task SleepAsync(string id, TimeSpan duration) => throw new NotSupportedException();

    public Task SleepUntilAsync(string id, DateTimeOffset until) => throw new NotSupportedException();

    public Task<InngestEvent<TData>?> WaitForEventAsync<TData>(string id, WaitForEventOptions options) => throw new NotSupportedException();

    public Task<TResult> InvokeAsync<TResult>(string id, InvokeOptions options) => throw new NotSupportedException();

    public Task<SendEventsResult> SendEventAsync(string id, IReadOnlyList<InngestEvent> events) => throw new NotSupportedException();
}
