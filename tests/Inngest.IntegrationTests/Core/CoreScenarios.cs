using System.Text;
using System.Text.Json;
using Inngest.IntegrationTests.Evaluation.Functions;
using Inngest.IntegrationTests.Evaluation.Harness;
using Inngest.IntegrationTests.Harness;

namespace Inngest.IntegrationTests.Core;

// What the server sends with inngest/function.cancelled and inngest/function.finished, a cron trigger with jitter at
// sync, the cause of a step failure after a real round trip, and payload transforms that seal what Inngest stores.
public sealed class CoreScenarios(EvaluationEnvironment environment) : EvaluationScenario<CoreFixture>(environment)
{
    [Fact(Timeout = 60_000)]
    public async Task A_cleanup_function_receives_the_data_of_a_cancelled_run()
    {
        var eventId = await SendAsync(CoreFunctions.Import, new RunEvent(TestId));
        var run = await WaitForRunAsync(eventId);
        await WaitForStepAsync(run.RunId, StepOps.Sleep);

        await SendAsync(CoreFunctions.ImportStop, new RunEvent(TestId));
        var ended = await WaitForEndAsync(eventId);
        var seen = (await WaitForObservationsAsync("cancelled"))[0].Value.ShouldBeOfType<SystemEventSeen>();

        Show("inngest/function.cancelled data", seen.RawData);
        ended.Status.ShouldBe(RunStatus.Cancelled);
        seen.FunctionId.ShouldBe($"{CoreFunctions.AppId}-import");
        seen.RunId.ShouldBe(run.RunId);
    }

    [Fact(Timeout = 60_000)]
    public async Task A_function_on_finished_runs_sees_the_result_of_a_run_that_completed()
    {
        var run = await RunToEndAsync(CoreFunctions.Report, new ReportEvent(TestId, Fail: false));
        var seen = (await WaitForObservationsAsync("finished"))[0].Value.ShouldBeOfType<FinishedSeen>();

        Show("inngest/function.finished data", Observed.Values<string>("finished-raw")[0]);
        run.Status.ShouldBe(RunStatus.Completed);
        seen.RunId.ShouldBe(run.RunId);
        seen.Result.ShouldNotBeNull();
        JsonDocument.Parse(seen.Result).RootElement.ShouldContainJson("""{"rows":3}""");
        seen.Error.ShouldBeNull();
    }

    [Fact(Timeout = 60_000)]
    public async Task A_function_on_finished_runs_sees_the_error_of_a_run_that_failed()
    {
        var run = await RunToEndAsync(CoreFunctions.Report, new ReportEvent(TestId, Fail: true));
        var seen = (await WaitForObservationsAsync("finished"))[0].Value.ShouldBeOfType<FinishedSeen>();

        Show("inngest/function.finished data", Observed.Values<string>("finished-raw")[0]);
        run.Status.ShouldBe(RunStatus.Failed);
        seen.RunId.ShouldBe(run.RunId);
        var error = seen.Error.ShouldNotBeNull();
        error.Name.ShouldBe("NonRetriableException");
        error.Message.ShouldBe("report failed");
        error.Cause.ShouldNotBeNull().Name.ShouldBe("InvalidDataException");

        // Inngest also reports the body of the failed call as the result.
        JsonDocument.Parse(seen.Result.ShouldNotBeNull()).RootElement.ShouldContainJson("""{"name":"NonRetriableException","message":"report failed"}""");
    }

    [Fact(Timeout = 60_000)]
    public async Task A_cron_trigger_with_jitter_is_accepted_at_sync()
    {
        var functions = await Api.GetAppFunctionsAsync(CoreFunctions.AppId, Cancellation);
        var trigger = functions.Single(function => function.Slug == $"{CoreFunctions.AppId}-{CoreFunctions.JitterId}").Config.GetProperty("triggers")[0];

        Show("the stored trigger", trigger.GetRawText());
        trigger.GetProperty("cron").GetString().ShouldBe("0 0 1 1 *");
        trigger.GetProperty("jitter").GetString().ShouldBe("5m");
        (await Api.GetAppsAsync(Cancellation)).Single(app => app.ExternalId == CoreFunctions.AppId).Error.ShouldBeNull();
    }

    [Fact(Timeout = 60_000)]
    public async Task A_caught_step_failure_exposes_its_cause_after_a_round_trip()
    {
        var run = await RunToEndAsync(CoreFunctions.Charge, new RunEvent(TestId));

        run.Status.ShouldBe(RunStatus.Completed);
        run.OutputAs<CaughtFailure>().ShouldBe(
            new CaughtFailure("InvalidOperationException", "declined", nameof(FunctionErrorException), "TimeoutException", "gateway timed out"));
    }

    [Fact(Timeout = 60_000)]
    public async Task A_rethrown_step_failure_fails_the_run_with_its_whole_cause_chain()
    {
        var run = await RunToEndAsync(CoreFunctions.ChargeRethrow, new RunEvent(TestId));

        run.Status.ShouldBe(RunStatus.Failed);
        run.Output.ShouldNotBeNull().ShouldContainJson(
            """{"name":"InvalidOperationException","message":"declined","cause":{"name":"TimeoutException","message":"gateway timed out"}}""");
    }

    [Fact(Timeout = 60_000)]
    public async Task Sealed_step_output_and_event_data_are_stored_sealed_and_read_back_open()
    {
        var run = await RunToEndAsync(CoreFunctions.Sealed, new RunEvent(TestId));
        await WaitForObservationsAsync("note");

        run.Status.ShouldBe(RunStatus.Completed);

        // Inngest handed the step's result back sealed, and the function read it open.
        var memoized = Observed.Values<string>("memoized").First(value => value.StartsWith("secret=", StringComparison.Ordinal))["secret=".Length..];
        memoized.ShouldNotContain("s3cret");
        Encoding.UTF8.GetString(SealingMiddleware.Open(Parse(memoized))).ShouldBe("\"s3cret value\"");
        Observed.Values<string>("secret").ShouldNotBeEmpty();
        Observed.Values<string>("secret").ShouldAllBe(value => value == "s3cret value");

        // The output Inngest stored is sealed.
        var output = run.Output.ShouldNotBeNull();
        SealingMiddleware.IsSealed(output).ShouldBeTrue();
        Parse(Encoding.UTF8.GetString(SealingMiddleware.Open(output))).ShouldContainJson($$"""{"testId":"{{TestId}}","encrypted":"s3cret value"}""");

        // The receiver got the event with its field sealed and read it open.
        SealingMiddleware.IsSealed(Parse(Observed.Values<string>("received").ShouldHaveSingleItem())).ShouldBeTrue();
        Observed.Values<string>("note").ShouldBe(["s3cret value"]);
    }

    private static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static void Show(string what, string json) => TestContext.Current.TestOutputHelper?.WriteLine($"{what}: {json}");
}
