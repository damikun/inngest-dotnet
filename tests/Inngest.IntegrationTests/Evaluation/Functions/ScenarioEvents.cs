using Inngest.IntegrationTests.Harness;
using Microsoft.Extensions.DependencyInjection;

namespace Inngest.IntegrationTests.Evaluation.Functions;

// The data of every event a scenario sends carries the scenario's test id. Functions record what they observe under it,
// and flow control keys use it, so scenarios share an app and still run in parallel.
internal interface IScenarioEvent
{
    string TestId { get; }
}

internal sealed record RunEvent(string TestId) : IScenarioEvent;

internal sealed record ValueEvent(string TestId, int Value) : IScenarioEvent;

internal sealed record TextEvent(string TestId, string Text) : IScenarioEvent;

internal static class ScenarioContext
{
    // What the functions record for the scenario that sent the run's event.
    public static TestRecording Observed<TEvent>(this FunctionContext<TEvent> context)
        where TEvent : IScenarioEvent =>
        context.Services.GetRequiredService<Probe>().For(context.Event.Data.TestId);

    // A step or function body that records name and then fails. The int result type only gives the lambda a type.
    public static int RecordThenThrow(this TestRecording observed, string name, object? value, Exception exception)
    {
        observed.Record(name, value);
        throw exception;
    }
}
