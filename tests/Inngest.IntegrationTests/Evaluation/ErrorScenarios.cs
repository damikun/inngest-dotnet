using Inngest.IntegrationTests.Evaluation.Functions;
using Inngest.IntegrationTests.Evaluation.Harness;
using Inngest.IntegrationTests.Harness;

namespace Inngest.IntegrationTests.Evaluation;

// The server retries failed calls after one second (--retry-interval 1), unless the SDK asks for a later time.
[Trait("Category", "Evaluation")]
[Trait("Area", "Errors and retries")]
public sealed class RetryScenarios(EvaluationEnvironment environment) : EvaluationScenario<EvaluationDevFixture>(environment)
{
    [Fact(Timeout = 60_000)]
    public async Task A_function_is_retried_with_increasing_attempt_numbers_until_it_succeeds()
    {
        var run = await RunToEndAsync(RetryFunctions.Eventual, new RunEvent(TestId), TimeSpan.FromSeconds(20));

        run.Status.ShouldBe(RunStatus.Completed);
        run.OutputAs<int>().ShouldBe(2);
        var attempts = Observed.Values<AttemptSeen>("attempt");
        attempts.Select(seen => seen.Attempt).ToList().ShouldClimbTo(2);
        attempts.ShouldAllBe(seen => seen.MaxAttempts == 4);
    }

    [Fact(Timeout = 60_000)]
    public async Task A_function_that_keeps_failing_gets_exactly_its_retries_and_then_fails()
    {
        var run = await RunToEndAsync(RetryFunctions.Exhausted, new RunEvent(TestId), TimeSpan.FromSeconds(20));

        run.Status.ShouldBe(RunStatus.Failed);
        run.Error.Message.ShouldBe("attempt 2 failed");
        Observed.Values<int>("attempt").ShouldClimbTo(2);
    }

    [Fact(Timeout = 60_000)]
    public async Task Zero_retries_means_one_attempt()
    {
        var run = await RunToEndAsync(RetryFunctions.NoRetries, new RunEvent(TestId));

        run.Status.ShouldBe(RunStatus.Failed);
        run.Error.Message.ShouldBe("no second chance");
        await Task.Delay(TimeSpan.FromSeconds(1.5), Cancellation);
        Observed.Values<int>("attempt").ShouldBe([0]);
    }

    [Fact(Timeout = 60_000)]
    public async Task NonRetriableException_in_a_step_fails_the_run_after_one_attempt()
    {
        var run = await RunToEndAsync(RetryFunctions.StepNonRetriable, new RunEvent(TestId));

        run.Status.ShouldBe(RunStatus.Failed);
        run.Error.Name.ShouldBe(nameof(NonRetriableException));
        run.Error.Message.ShouldBe("the input is unusable");
        Observed.Named("validate").Count.ShouldBe(1);
    }

    [Fact(Timeout = 60_000)]
    public async Task NonRetriableException_in_the_function_fails_the_run_without_running_its_steps_again()
    {
        var run = await RunToEndAsync(RetryFunctions.FunctionNonRetriable, new RunEvent(TestId));

        run.Status.ShouldBe(RunStatus.Failed);
        run.Error.Message.ShouldBe("gave up after the step");
        Observed.Named("done").Count.ShouldBe(1);
        Observed.Named("throw").Count.ShouldBe(1);
    }

    // The server reads the time to the second and the Docker VM's clock is a few hundred milliseconds off the host's,
    // so the retry may come up to a second early; without the time it would follow after about one second.
    [Fact(Timeout = 60_000)]
    public async Task RetryAfterException_with_a_time_holds_the_retry_back_until_then()
    {
        var run = await RunToEndAsync(RetryFunctions.RetryAt, new RunEvent(TestId), TimeSpan.FromSeconds(20));

        run.Status.ShouldBe(RunStatus.Completed);
        var attempts = Observed.Named("attempt");
        attempts.Count.ShouldBe(2);
        (attempts[1].At - attempts[0].At).ShouldBeInRange(RetryFunctions.RetryDelay - TimeSpan.FromSeconds(1), RetryFunctions.RetryDelay + TimeSpan.FromSeconds(10));
    }

    [Fact(Timeout = 60_000)]
    public async Task RetryAfterException_in_a_step_holds_the_step_retry_back()
    {
        var run = await RunToEndAsync(RetryFunctions.StepRetryAfter, new RunEvent(TestId), TimeSpan.FromSeconds(20));

        run.Status.ShouldBe(RunStatus.Completed);
        run.OutputAs<string>().ShouldBe("through");
        var attempts = Observed.Named("attempt");
        attempts.Count.ShouldBe(2);
        (attempts[1].At - attempts[0].At).ShouldBeGreaterThan(RetryFunctions.RetryDelay - TimeSpan.FromSeconds(1));
    }

    [Fact(Timeout = 60_000)]
    public async Task Every_step_has_a_retry_budget_of_its_own_and_counts_attempts_from_zero()
    {
        var run = await RunToEndAsync(RetryFunctions.StepBudget, new RunEvent(TestId), TimeSpan.FromSeconds(20));

        run.Status.ShouldBe(RunStatus.Completed);
        run.OutputAs<string>().ShouldBe("all three recovered");

        // Each step succeeds on its attempt 1: with a counter shared by the run, b and c would see higher numbers.
        Observed.Values<int>("a").ShouldClimbTo(1);
        Observed.Values<int>("b").ShouldClimbTo(1);
        Observed.Values<int>("c").ShouldClimbTo(1);
    }
}

[Trait("Category", "Evaluation")]
[Trait("Area", "Errors and retries")]
public sealed class FailureScenarios(EvaluationEnvironment environment) : EvaluationScenario<EvaluationDevFixture>(environment)
{
    [Fact(Timeout = 60_000)]
    public async Task The_final_error_carries_the_exception_type_message_stack_and_cause()
    {
        var run = await RunToEndAsync(FailureFunctions.Details, new RunEvent(TestId), TimeSpan.FromSeconds(20));

        run.Status.ShouldBe(RunStatus.Failed);
        var output = run.Output.ShouldNotBeNull();
        output.ShouldContainJson("""
            {
              "name": "PaymentDeclinedException",
              "message": "card 4242 was declined",
              "cause": { "name": "TimeoutException", "message": "the payment gateway timed out" }
            }
            """);
        output.GetProperty("stack").GetString().ShouldNotBeNull().ShouldContain(nameof(FailureFunctions));
    }

    [Fact(Timeout = 60_000)]
    public async Task A_failure_handler_runs_once_with_the_error_and_the_original_event()
    {
        var run = await RunToEndAsync(FailureFunctions.Handled, new RunEvent(TestId), TimeSpan.FromSeconds(20));

        run.Status.ShouldBe(RunStatus.Failed);
        Observed.Values<int>("attempt").ShouldClimbTo(1);
        var handled = (await WaitForObservationsAsync("on-failure", timeout: TimeSpan.FromSeconds(20))).ShouldHaveSingleItem();
        handled.Value.ShouldBe(new HandledFailure(
            "inngest/function.failed",
            "eval-err-handled",
            run.RunId,
            nameof(InvalidOperationException),
            $"handled failure {TestId}",
            FailureFunctions.Handled));
        await Task.Delay(TimeSpan.FromSeconds(1.5), Cancellation);
        Observed.Named("on-failure").Count.ShouldBe(1);
    }

    [Fact(Timeout = 60_000)]
    public async Task A_failure_handler_can_run_steps_and_sleep()
    {
        var run = await RunToEndAsync(FailureFunctions.HandledWithSteps, new RunEvent(TestId));

        run.Status.ShouldBe(RunStatus.Failed);
        await WaitForObservationsAsync("cleanup", timeout: TimeSpan.FromSeconds(20));
        Observed.Names.ShouldBe(["notify", "cleanup"]);
        (Observed.Named("cleanup")[0].At - Observed.Named("notify")[0].At).ShouldBeGreaterThan(TimeSpan.FromMilliseconds(700));
    }

    [Fact(Timeout = 60_000)]
    public async Task A_step_result_that_cannot_be_serialized_fails_the_step_and_can_be_caught()
    {
        var run = await RunToEndAsync(FailureFunctions.Unserializable, new RunEvent(TestId), TimeSpan.FromSeconds(20));

        run.Status.ShouldBe(RunStatus.Completed);
        var caught = run.OutputAs<CaughtResult>();
        caught.Caught.ShouldBeTrue();
        caught.StepId.ShouldBe("cyclic");
        caught.ErrorName.ShouldBe("JsonException");
        caught.Message.ShouldNotBeNull().ShouldContain("cycle");
        Observed.Values<int>("cyclic").ShouldClimbTo(1);
    }

    [Fact(Timeout = 60_000)]
    public async Task A_parallel_step_that_fails_raises_StepFailedException_while_the_others_succeed()
    {
        var run = await RunToEndAsync(FailureFunctions.ParallelFailure, new RunEvent(TestId));

        run.Status.ShouldBe(RunStatus.Completed);
        run.OutputAs<ParallelFailure>().ShouldBe(new ParallelFailure("broken", "broken for good", 1, 1));
        Observed.Names.Order().ShouldBe(["broken", "first", "last"]);
    }
}
