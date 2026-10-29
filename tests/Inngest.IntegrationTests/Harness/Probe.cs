using System.Collections.Concurrent;

namespace Inngest.IntegrationTests.Harness;

// What the test functions observed while they ran, keyed by the test id that every test event carries. A singleton in
// the app under test. Functions record from inside step bodies, so a count is how often a body really executed, not
// how often the function was replayed.
public sealed class Probe
{
    private readonly ConcurrentDictionary<string, TestRecording> _recordings = new();

    public TestRecording For(string testId) => _recordings.GetOrAdd(testId, static _ => new TestRecording());
}

public sealed class TestRecording
{
    private readonly Lock _lock = new();
    private readonly List<Observation> _observations = [];

    // Returns how many times name has been recorded, this time included.
    public int Record(string name, object? value = null)
    {
        lock (_lock)
        {
            _observations.Add(new Observation(name, value, DateTimeOffset.UtcNow));
            return _observations.Count(observation => observation.Name == name);
        }
    }

    public IReadOnlyList<Observation> All
    {
        get
        {
            lock (_lock)
            {
                return [.. _observations];
            }
        }
    }

    public IReadOnlyList<string> Names => [.. All.Select(observation => observation.Name)];

    public IReadOnlyList<Observation> Named(string name) => [.. All.Where(observation => observation.Name == name)];

    public IReadOnlyList<T> Values<T>(string name) => [.. Named(name).Select(observation => (T)observation.Value!)];

    public async Task<Observation> WaitForAsync(string name, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var observations = await Poll.UntilAsync(
            $"the test functions to record \"{name}\"",
            _ => Named(name),
            named => named.Count > 0,
            timeout,
            cancellationToken);

        return observations[0];
    }

    public override string ToString() => $"[{string.Join(", ", All)}]";
}

public sealed record Observation(string Name, object? Value, DateTimeOffset At)
{
    public override string ToString() => Value is null ? $"{Name} at {At:HH:mm:ss.fff}" : $"{Name}={Value} at {At:HH:mm:ss.fff}";
}
