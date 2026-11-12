using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;

namespace Inngest.IntegrationTests.Functions;

// An app service that logs through its own ILogger<T> and knows nothing of Inngest, so whatever run context its records
// carry comes from the scopes the SDK opens.
internal sealed class LogScopeService(ILogger<LogScopeService> logger)
{
    public string Log(string testId, string where)
    {
        logger.LogInformation("Log scope check {TestId} at {Where}", testId, where);
        return where;
    }

    public static string Message(string testId, string where) => $"Log scope check {testId} at {where}";
}

// A function that logs through the service from inside a step and from its body after the step, which each host's
// fixture registers under an id and event of its own.
internal static class LogScopeFunctions
{
    public const string StepId = "log";

    public static IInngestBuilder AddLogScopeFunction(this IInngestBuilder inngest, string id, string eventName, CheckpointingOptions? checkpointing = null)
    {
        inngest.Services.TryAddSingleton<LogScopeService>();
        return inngest.AddFunction<TestEvent>(
            new FunctionOptions { Id = id, Triggers = [Trigger.Event(eventName)], Checkpointing = checkpointing },
            static async (context, _) =>
            {
                var service = context.Services.GetRequiredService<LogScopeService>();
                var testId = context.Event.Data.TestId;
                await context.Step.RunAsync(StepId, () => service.Log(testId, "step"));
                return new TextResult(service.Log(testId, "body"));
            });
    }

    // The properties of every scope the service's one record for the test and place was logged in.
    public static Dictionary<string, object?> ScopeOf(this FakeLogCollector logs, string testId, string where)
    {
        var record = logs.GetSnapshot().Where(record => record.Message == LogScopeService.Message(testId, where)).ShouldHaveSingleItem();
        var properties = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var scope in record.Scopes.OfType<IEnumerable<KeyValuePair<string, object?>>>())
        {
            foreach (var (key, value) in scope)
            {
                properties[key] = value;
            }
        }

        return properties;
    }

    // The run's context, with the dev server's page of the run, as every host must give it on the first attempt.
    public static void ShouldDescribeRun(this Dictionary<string, object?> scope, string functionId, string runId, string eventName, Uri serverUrl)
    {
        scope.ShouldContainKeyAndValue("functionID", functionId);
        scope.ShouldContainKeyAndValue("runID", runId);
        scope.ShouldContainKeyAndValue("eventName", eventName);
        scope.ShouldContainKeyAndValue("attempt", 0);
        scope.ShouldContainKeyAndValue("runURL", $"{serverUrl.GetLeftPart(UriPartial.Authority)}/run?runID={runId}");
    }

    // The scopes of the step's record and of the body's record after it, for a run of a function added above.
    public static void ShouldHaveLoggedWithRunScopes(this FakeLogCollector logs, string testId, string functionId, string runId, string eventName, Uri serverUrl)
    {
        var step = logs.ScopeOf(testId, "step");
        step.ShouldDescribeRun(functionId, runId, eventName, serverUrl);
        step.ShouldContainKeyAndValue("stepID", StepId);

        var body = logs.ScopeOf(testId, "body");
        body.ShouldDescribeRun(functionId, runId, eventName, serverUrl);
        body.ShouldNotContainKey("stepID");
    }
}
