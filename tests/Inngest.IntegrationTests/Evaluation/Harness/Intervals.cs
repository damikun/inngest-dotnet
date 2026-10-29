namespace Inngest.IntegrationTests.Evaluation.Harness;

// When a piece of work ran, as the function recorded it on the app's clock.
public readonly record struct Interval(DateTimeOffset Start, DateTimeOffset End)
{
    public TimeSpan Duration => End - Start;
}

public static class Intervals
{
    // The most intervals that were open at the same moment. An interval that ends exactly when another starts does not
    // overlap it.
    public static int MaxOverlap(IEnumerable<Interval> intervals)
    {
        var edges = intervals
            .SelectMany(interval => new[] { (At: interval.Start, Delta: 1), (At: interval.End, Delta: -1) })
            .OrderBy(edge => edge.At)
            .ThenBy(edge => edge.Delta);

        int open = 0, max = 0;
        foreach (var (_, delta) in edges)
        {
            open += delta;
            max = Math.Max(max, open);
        }

        return max;
    }
}

public static class Attempts
{
    // Attempt numbers as a function saw them. A call the server could not deliver, for example while Docker's
    // networking is saturated, still uses up an attempt, so a number can be missing. What must hold is that they only go
    // up and that the run ends on the attempt the scenario expects.
    public static void ShouldClimbTo(this IReadOnlyList<int> attempts, int last)
    {
        attempts.ShouldNotBeEmpty();
        attempts.Zip(attempts.Skip(1)).ShouldAllBe(pair => pair.Second > pair.First);
        attempts[^1].ShouldBe(last);
    }
}
