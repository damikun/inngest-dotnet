namespace Inngest.Aspire.Hosting.Tests.Containers;

// Waits for something that happens in a container or a process the AppHost started, which no event announces.
internal static class Poll
{
    public static async Task<T> UntilAsync<T>(string what, Func<CancellationToken, Task<T?>> read, TimeSpan timeout, CancellationToken cancellationToken)
        where T : class
    {
        using var timeLimit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeLimit.CancelAfter(timeout);
        Exception? lastFailure = null;
        while (true)
        {
            try
            {
                if (await read(timeLimit.Token) is { } value)
                {
                    return value;
                }
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                // Whatever a server or an app that is still starting answers with: a refused connection, a timeout, a body
                // that is not JSON yet, or an app that is not known yet. Only the cancellation of the test itself ends the wait
                // early, and running out of time ends it with the last of these.
                lastFailure = exception;
            }

            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250), timeLimit.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException($"Waited {timeout.TotalSeconds:0} s for {what}.", lastFailure);
            }
        }
    }
}
