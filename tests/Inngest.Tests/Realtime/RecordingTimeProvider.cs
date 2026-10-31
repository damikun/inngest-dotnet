using Microsoft.Extensions.Time.Testing;

namespace Inngest.Tests.Realtime;

// Fake time that remembers how long each timer was set for.
internal sealed class RecordingTimeProvider(DateTimeOffset start) : FakeTimeProvider(start)
{
    private readonly List<TimeSpan> _timers = [];

    public IReadOnlyList<TimeSpan> Timers
    {
        get
        {
            lock (_timers)
            {
                return [.. _timers];
            }
        }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        lock (_timers)
        {
            _timers.Add(dueTime);
        }

        return base.CreateTimer(callback, state, dueTime, period);
    }
}
