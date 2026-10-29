using System.Collections;

namespace Inngest.IntegrationTests.Harness;

public static class Poll
{
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(100);

    // Calls read until done accepts what it returned. A timeout fails with what was awaited, the last value seen and
    // whatever describe adds (server log, sync state), so the failure explains itself. Connection errors count as
    // "not yet"; any other exception from read fails at once.
    public static async Task<T> UntilAsync<T>(
        string what,
        Func<CancellationToken, Task<T>> read,
        Func<T, bool> done,
        TimeSpan timeout,
        Func<CancellationToken, Task<string>>? describe,
        CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);

        object? lastSeen = null;
        while (true)
        {
            try
            {
                var value = await read(deadline.Token);
                if (done(value))
                {
                    return value;
                }

                lastSeen = value;
                await Task.Delay(Interval, deadline.Token);
            }
            catch (HttpRequestException exception)
            {
                lastSeen = exception;
                if (!await DelayAsync(deadline.Token))
                {
                    break;
                }
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                break;
            }
        }

        var details = describe is null ? string.Empty : await describe(CancellationToken.None);
        throw new TimeoutException(
            $"Timed out after {timeout.TotalSeconds:0.#} s waiting for {what}.\nLast seen: {Format(lastSeen)}\n{details}");
    }

    public static Task<T> UntilAsync<T>(
        string what,
        Func<CancellationToken, T> read,
        Func<T, bool> done,
        TimeSpan timeout,
        CancellationToken cancellationToken) =>
        UntilAsync(what, token => Task.FromResult(read(token)), done, timeout, describe: null, cancellationToken);

    private static async Task<bool> DelayAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(Interval, cancellationToken);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private static string Format(object? value) => value switch
    {
        null => "nothing",
        string text => text,
        Exception exception => $"{exception.GetType().Name}: {exception.Message}",
        IEnumerable items => $"[{string.Join(", ", items.Cast<object?>())}]",
        _ => value.ToString() ?? "nothing",
    };
}
