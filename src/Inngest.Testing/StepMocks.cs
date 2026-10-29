// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.
// Ported from the Inngest TypeScript SDK (inngest-js, Copyright 2022 Inngest Inc.) and modified for .NET.

using System.Collections.Immutable;

namespace Inngest.Testing;

// What the test host answers in place of steps, by the id the code passes; a repeated id is addressed with its
// ":<n>" suffix, such as "poll:1". A mocked run step still goes through the engine, so its value is serialized,
// memoized and replayed like a real result. Every method returns a new set, so a shared set can be extended per
// test.
public sealed class StepMocks
{
    private readonly ImmutableDictionary<string, StepMock> _steps;

    public StepMocks()
        : this(ImmutableDictionary.Create<string, StepMock>(StringComparer.Ordinal), endsAllSleeps: false)
    {
    }

    private StepMocks(ImmutableDictionary<string, StepMock> steps, bool endsAllSleeps)
    {
        _steps = steps;
        EndsAllSleeps = endsAllSleeps;
    }

    internal bool EndsAllSleeps { get; }

    // The run step returns result instead of running its handler.
    public StepMocks Run<T>(string stepId, T result) => With(stepId, new RunMock(_ => result));

    // Called instead of the step's handler on every attempt, with the attempt number counted from zero. Throw from
    // it to fail the attempt.
    public StepMocks Run<T>(string stepId, Func<int, T> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return With(stepId, new RunMock(attempt => handler(attempt)));
    }

    // The run step throws on every attempt, so it is retried and then fails for good, at once for a
    // NonRetriableException.
    public StepMocks RunThrows(string stepId, Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return With(stepId, new RunMock(_ => throw exception));
    }

    // The sleep ends. When the app's TimeProvider is a FakeTimeProvider, the host moves it to the end of the sleep.
    public StepMocks Sleep(string stepId) => With(stepId, SleepMock.Instance);

    // Every sleep ends, as Sleep does for one.
    public StepMocks AllSleeps() => new(_steps, endsAllSleeps: true);

    // The wait receives this event, which must have the name the step waits for.
    public StepMocks WaitForEvent(string stepId, InngestEvent received)
    {
        ArgumentNullException.ThrowIfNull(received);
        return With(stepId, new WaitMock(received));
    }

    // The wait times out, and the step returns null.
    public StepMocks WaitForEventTimeout(string stepId) => With(stepId, new WaitMock(null));

    // The invoked function returns result.
    public StepMocks Invoke<T>(string stepId, T result) => With(stepId, InvokeMock.Returning(result));

    // The invoked function fails with exception, which the invoking function receives as a StepFailedException.
    public StepMocks InvokeThrows(string stepId, Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return With(stepId, InvokeMock.Throwing(exception));
    }

    internal StepMock? Find(string stepId) => _steps.GetValueOrDefault(stepId);

    private StepMocks With(string stepId, StepMock mock)
    {
        ArgumentException.ThrowIfNullOrEmpty(stepId);
        return new StepMocks(_steps.SetItem(stepId, mock), EndsAllSleeps);
    }
}
