using System.Collections.Concurrent;

namespace Inngest.IntegrationTests.Evaluation.Harness;

// Lets a scenario hold a function inside a step until it opens the gate, for orderings that timing alone cannot make
// deterministic. A singleton in the app under test; gates are keyed by test id and name.
public sealed class Gates
{
    private readonly ConcurrentDictionary<string, TaskCompletionSource> _gates = new(StringComparer.Ordinal);

    public void Open(string testId, string name) => Get(testId, name).TrySetResult();

    // Waits for the gate, but never longer than timeout, so a scenario that fails before opening it cannot leave a
    // request to the app hanging.
    public async Task WaitAsync(string testId, string name, TimeSpan timeout, CancellationToken cancellationToken)
    {
        try
        {
            await Get(testId, name).Task.WaitAsync(timeout, cancellationToken);
        }
        catch (TimeoutException)
        {
            throw new InvalidOperationException($"Gate {name} of test {testId} was not opened within {timeout}.");
        }
    }

    private TaskCompletionSource Get(string testId, string name) =>
        _gates.GetOrAdd($"{testId}/{name}", static _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
}
