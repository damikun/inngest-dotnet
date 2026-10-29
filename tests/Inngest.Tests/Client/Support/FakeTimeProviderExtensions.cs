using Microsoft.Extensions.Time.Testing;

namespace Inngest.Tests.Client;

internal static class FakeTimeProviderExtensions
{
    // Backoff waits go through the time provider, so a test moves the clock until the operation gets through them.
    public static async Task<T> AdvanceUntilCompletedAsync<T>(this FakeTimeProvider time, Task<T> operation)
    {
        await time.AdvanceUntilCompletedAsync((Task)operation);
        return await operation;
    }

    public static async Task AdvanceUntilCompletedAsync(this FakeTimeProvider time, Task operation)
    {
        using var giveUp = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (!operation.IsCompleted)
        {
            time.Advance(TimeSpan.FromSeconds(1));
            await Task.Delay(1, giveUp.Token);
        }

        await operation.WaitAsync(giveUp.Token);
    }
}
