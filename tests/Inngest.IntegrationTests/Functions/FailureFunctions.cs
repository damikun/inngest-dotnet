using Inngest.IntegrationTests.Harness;

namespace Inngest.IntegrationTests.Functions;

// Fails every attempt. Its failure handler runs as the separate function "always-fails-failure", triggered by the
// inngest/function.failed event the server emits once the last attempt has failed.
internal sealed class AlwaysFailsFunction(Probe probe) : InngestFunction<TestEvent>, IFailureHandler
{
    public static FunctionOptions Options { get; } = new()
    {
        Id = "always-fails",
        Retries = 1,
        Triggers = [Trigger.Event("itests/always.fails")],
    };

    public static string MessageFor(string testId) => $"Always fails ({testId})";

    public override Task<object?> ExecuteAsync(FunctionContext<TestEvent> context, CancellationToken cancellationToken)
    {
        probe.For(context.Event.Data.TestId).Record("attempt", context.Attempt);
        throw new InvalidOperationException(MessageFor(context.Event.Data.TestId));
    }

    public Task OnFailureAsync(FunctionContext<FunctionFailure> context, CancellationToken cancellationToken)
    {
        var failure = context.Event.Data;
        var original = failure.Event;
        var testId = original.GetProperty("data").GetProperty("testId").GetString()!;
        probe.For(testId).Record("on-failure", new FailureSeen(
            context.Event.Name,
            failure.FunctionId,
            failure.RunId,
            failure.Error.Name,
            failure.Error.Message,
            original.TryGetProperty("name", out var name) ? name.GetString() : null));

        return Task.CompletedTask;
    }
}
