using Microsoft.Extensions.Time.Testing;

namespace Inngest.Tests.Connect.Support;

// Fake time that also tells a test when the worker has started a wait, so that the test moves the clock only once
// the timer it means to fire exists.
internal sealed class TestTime(DateTimeOffset start) : FakeTimeProvider(start)
{
    private readonly Lock _gate = new();
    private readonly List<TimeSpan> _created = [];
    private readonly List<(Func<TimeSpan, bool> Match, TaskCompletionSource<TimeSpan> Created)> _waiters = [];

    public IReadOnlyList<TimeSpan> CreatedTimers
    {
        get
        {
            lock (_gate)
            {
                return [.. _created];
            }
        }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = base.CreateTimer(callback, state, dueTime, period);
        lock (_gate)
        {
            _created.Add(dueTime);
            for (var i = _waiters.Count - 1; i >= 0; i--)
            {
                if (_waiters[i].Match(dueTime))
                {
                    _waiters[i].Created.TrySetResult(dueTime);
                    _waiters.RemoveAt(i);
                }
            }
        }

        return timer;
    }

    // Completes when a timer due after this long is created, counting only timers created from now on.
    public Task<TimeSpan> NextTimerAsync(TimeSpan dueTime) => NextTimerAsync(due => due == dueTime, $"{dueTime}");

    public async Task<TimeSpan> NextTimerAsync(Func<TimeSpan, bool> match, string description)
    {
        var created = new TaskCompletionSource<TimeSpan>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            _waiters.Add((match, created));
        }

        try
        {
            return await created.Task.WaitAsync(Waits.Timeout, TestContext.Current.CancellationToken);
        }
        catch (TimeoutException exception)
        {
            throw new TimeoutException($"The worker did not start a wait of {description}; it started [{string.Join(", ", CreatedTimers)}].", exception);
        }
    }
}
