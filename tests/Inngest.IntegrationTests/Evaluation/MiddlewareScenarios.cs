using Inngest.IntegrationTests.Evaluation.Functions;
using Inngest.IntegrationTests.Evaluation.Harness;
using Inngest.IntegrationTests.Harness;
using Microsoft.Extensions.Logging;

namespace Inngest.IntegrationTests.Evaluation;

// The middleware app registers a recording middleware, which records every observer hook under the run id, and a
// transforming one, which changes results of the transform and send functions only.
[Trait("Category", "Evaluation")]
[Trait("Area", "Middleware and logging")]
public sealed class MiddlewareScenarios(EvaluationEnvironment environment) : EvaluationScenario<MiddlewareFixture>(environment)
{
    // Three calls: the first runs step a, the second step b, the third returns. Memoized steps raise no step hooks, and
    // the run start is only seen by the first attempt of the first call, so it is missing when that call never reached
    // the app.
    [Fact(Timeout = 60_000)]
    public async Task Observer_hooks_fire_once_per_step_and_run_in_lifecycle_order()
    {
        var run = await RunToEndAsync(MiddlewareFunctions.Observed, new RunEvent(TestId));

        run.Status.ShouldBe(RunStatus.Completed);
        var hooks = HooksOf(run);
        List<string> expected =
        [
            "memoization-end:",
            "run-start:",
            "step-start:a",
            "step-complete:a=A",
            "memoization-end:",
            "step-start:b",
            "step-complete:b=B",
            "memoization-end:",
            "run-complete:done",
        ];
        if (hooks[0].Attempt > 0)
        {
            expected.Remove("run-start:");
        }

        hooks.Select(hook => $"{hook.Name}:{hook.Detail}").ShouldBe(expected);

        // A new middleware instance serves each call and stays the same for the whole call.
        var stepA = hooks.Single(hook => hook.Name == "step-start" && hook.Detail == "a").Instance;
        var stepB = hooks.Single(hook => hook.Name == "step-start" && hook.Detail == "b").Instance;
        hooks.Select(hook => hook.Instance).Distinct().Count().ShouldBe(3);
        hooks.Single(hook => hook.Name == "step-complete" && hook.Detail == "a=A").Instance.ShouldBe(stepA);
        stepB.ShouldNotBe(stepA);
    }

    [Fact(Timeout = 60_000)]
    public async Task Error_hooks_see_every_failed_attempt_and_which_one_was_final()
    {
        var run = await RunToEndAsync(MiddlewareFunctions.Failing, new RunEvent(TestId), TimeSpan.FromSeconds(20));

        run.Status.ShouldBe(RunStatus.Failed);
        var hooks = HooksOf(run);
        var stepErrors = hooks.Where(hook => hook.Name == "step-error").ToList();
        stepErrors.Select(hook => hook.Attempt).ToList().ShouldClimbTo(1);
        stepErrors[^1].Detail.ShouldBe("flaky:final");
        stepErrors.SkipLast(1).ShouldAllBe(hook => hook.Detail == "flaky:retry");

        // The uncaught step failure fails the call that replays it, which is final however many attempts are left.
        hooks.Where(hook => hook.Name == "run-error").Select(hook => hook.Detail).ShouldBe(["final"]);
        hooks.Count(hook => hook.Name == "run-start").ShouldBe(hooks[0].Attempt == 0 ? 1 : 0);
    }

    [Fact(Timeout = 60_000)]
    public async Task Wrapping_hooks_change_what_steps_memoize_and_what_the_function_returns()
    {
        var run = await RunToEndAsync(MiddlewareFunctions.Transform, new RunEvent(TestId));

        run.Status.ShouldBe(RunStatus.Completed);
        run.Output.ShouldNotBeNull().ShouldContainJson("""
            { "output": { "shout": "QUIET WORDS", "suffix": "plain (read by middleware)" }, "wrappedBy": "middleware" }
            """);

        // The handler's upper-casing is what Inngest memoized, so the step ran once; the suffix is added each time the
        // memoized result is read, never stacked onto what Inngest stored.
        Observed.Named("shout-ran").Count.ShouldBe(1);
        Observed.Values<string>("seen").ShouldBe(["QUIET WORDS / plain (read by middleware)"]);
    }

    [Fact(Timeout = 60_000)]
    public async Task Middleware_can_run_steps_of_its_own_around_the_function()
    {
        var run = await RunToEndAsync(MiddlewareFunctions.Audited, new RunEvent(TestId));

        run.Status.ShouldBe(RunStatus.Completed);
        run.OutputAs<string>().ShouldBe("worked");
        Observed.Names.ShouldBe(["audit-start", "work", "audit-end"]);
    }

    [Fact(Timeout = 60_000)]
    public async Task WrapSendEventAsync_can_add_data_to_the_events_a_step_sends()
    {
        var run = await RunToEndAsync(MiddlewareFunctions.Send, new ValueEvent(TestId, 3));

        run.Status.ShouldBe(RunStatus.Completed);
        var received = await WaitForEndAsync(run.OutputAs<string[]>().ShouldHaveSingleItem());
        received.Output.ShouldNotBeNull().ShouldContainJson($$"""{ "testId": "{{TestId}}", "value": 3, "stamp": "{{MiddlewareFunctions.Stamp}}" }""");
    }

    // The function logs before, between and after two steps, over three calls; each message appears once.
    [Fact(Timeout = 60_000)]
    public async Task The_function_logger_writes_each_message_once_per_run_with_the_run_id()
    {
        var run = await RunToEndAsync(MiddlewareFunctions.Logging, new RunEvent(TestId));

        run.Status.ShouldBe(RunStatus.Completed);
        var records = Fixture.App.Logs.GetSnapshot()
            .Where(record => record.Category == "Inngest.Function.mw-logging" && record.Message.Contains(TestId, StringComparison.Ordinal))
            .ToList();
        records.Select(record => record.Message).ShouldBe([$"start {TestId}", $"between {TestId}", $"end {TestId}"]);
        records.Select(record => record.Level).ShouldBe([LogLevel.Information, LogLevel.Information, LogLevel.Warning]);
        records.ShouldAllBe(record => record.Scopes.OfType<IEnumerable<KeyValuePair<string, object?>>>()
            .Any(scope => scope.Any(pair => pair.Key == "runID" && Equals(pair.Value, run.RunId))));
    }

    private List<Hook> HooksOf(RunInfo run) => [.. Fixture.Probe.For(run.RunId).All.Select(observation => (Hook)observation.Value!)];
}
