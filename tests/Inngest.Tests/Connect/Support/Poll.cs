namespace Inngest.Tests.Connect.Support;

internal static class Poll
{
    // For effects the worker has no message for, such as a log line.
    public static async Task UntilAsync(Func<bool> condition)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(Waits.Timeout);
        while (!condition())
        {
            await Task.Delay(10, timeout.Token);
        }
    }
}
