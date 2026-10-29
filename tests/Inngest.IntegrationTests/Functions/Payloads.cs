namespace Inngest.IntegrationTests.Functions;

// Event data. Every event carries the id of the test that sent it; functions key what they record in the probe by it,
// so tests can share one app and one server and still run in parallel.
internal sealed record TestEvent(string TestId);

internal sealed record GreetEvent(string TestId, string Name);

internal sealed record NumberEvent(string TestId, int Value);

internal sealed record SleepUntilEvent(string TestId, DateTimeOffset Until);

internal sealed record WaitStartEvent(string TestId, int TimeoutSeconds);

internal sealed record ResumeEvent(string TestId, string Message);

// Function and step output.
internal sealed record Greeting(string Message);

internal sealed record SequenceResult(int First, int Second, string Third);

internal sealed record AttemptResult(int Attempt);

internal sealed record TextResult(string Text);

internal sealed record NumberResult(int Value);

internal sealed record ParentResult(NumberResult Child);

internal sealed record CaughtFailure(bool Caught, string? StepId, string? Message);

internal sealed record ParallelResult(IReadOnlyList<int> Parts, int Total);

internal sealed record SentResult(IReadOnlyList<string> Ids);

internal sealed record WaitResult(bool Matched, string? Name, string? Message);

// What a failure handler saw, recorded in the probe of the test whose event made the function fail.
internal sealed record FailureSeen(
    string TriggerName,
    string FunctionId,
    string RunId,
    string ErrorName,
    string ErrorMessage,
    string? OriginalEventName);
