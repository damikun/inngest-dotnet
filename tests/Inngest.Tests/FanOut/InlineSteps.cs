namespace Inngest.Tests.FanOut;

// A fake like the one in the testing guide: each step runs its handler on the spot. A handler that throws is a step
// that failed for good, as Inngest reports it once the retries are used up.
internal sealed class InlineSteps : IStepTools
{
    private readonly List<string> _calls = [];

    public IReadOnlyList<string> Calls
    {
        get
        {
            lock (_calls)
            {
                return [.. _calls];
            }
        }
    }

    public async Task<T> RunAsync<T>(string id, Func<CancellationToken, Task<T>> handler)
    {
        lock (_calls)
        {
            _calls.Add(id);
        }

        try
        {
            return await handler(CancellationToken.None);
        }
        catch (Exception exception)
        {
            throw new StepFailedException(id, new FunctionError { Name = exception.GetType().Name, Message = exception.Message });
        }
    }

    public Task SleepAsync(string id, TimeSpan duration) => throw new NotSupportedException();

    public Task SleepUntilAsync(string id, DateTimeOffset until) => throw new NotSupportedException();

    public Task<InngestEvent<TData>?> WaitForEventAsync<TData>(string id, WaitForEventOptions options) => throw new NotSupportedException();

    public Task<TResult> InvokeAsync<TResult>(string id, InvokeOptions options) => throw new NotSupportedException();

    public Task<SendEventsResult> SendEventAsync(string id, IReadOnlyList<InngestEvent> events) => throw new NotSupportedException();
}
