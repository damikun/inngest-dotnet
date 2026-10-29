using System.Text.Json;
using Inngest.Testing;
using Microsoft.Extensions.DependencyInjection;
using static Inngest.Tests.Testing.TestHosts;

namespace Inngest.Tests.Testing;

// Retries and failures as Inngest handles them: the same call again with the next attempt number until the
// function's retries run out, then a final failure, a step failure the function sees, and the failure handler.
public sealed class TestHostFailureTests
{
    [Fact]
    public async Task A_failing_step_is_retried_with_the_attempt_numbers_inngest_sends()
    {
        var attempts = new List<int>();
        await using var host = For(async context =>
            await context.Step.RunAsync("flaky", () =>
            {
                attempts.Add(context.Attempt);
                return attempts.Count < 3 ? throw new HttpRequestException("connection reset") : "ok";
            }));

        var run = await host.RunAsync("fn", Event(), Cancel);

        run.GetOutput<string>().ShouldBe("ok");
        attempts.ShouldBe([0, 1, 2]);
        var step = run.GetStep("flaky");
        step.Status.ShouldBe(TestStepStatus.Completed);
        step.Attempts.ShouldBe(3);
        step.Errors.Count.ShouldBe(2);
        step.Errors.ShouldAllBe(error => error is HttpRequestException);
        step.Error.ShouldBeNull();
    }

    [Fact]
    public async Task A_step_that_keeps_failing_fails_the_run_once_its_retries_run_out()
    {
        await using var host = For(
            async context => await context.Step.RunAsync<string>("charge", _ => throw new InvalidOperationException("card declined")),
            retries: 2);

        var run = await host.RunAsync("fn", Event(), Cancel);

        run.Status.ShouldBe(TestRunStatus.Failed);
        var failure = run.Error.ShouldBeOfType<StepFailedException>();
        failure.StepId.ShouldBe("charge");
        failure.Error.Name.ShouldBe("InvalidOperationException");
        failure.Error.Message.ShouldBe("card declined");

        var step = run.GetStep("charge");
        step.Status.ShouldBe(TestStepStatus.Failed);
        step.Attempts.ShouldBe(3);
        step.Error.ShouldBeOfType<InvalidOperationException>().Message.ShouldBe("card declined");
        step.Errors.Count.ShouldBe(3);
        run.Output.ShouldBeNull();
    }

    [Fact]
    public async Task A_retry_after_error_is_retried_without_waiting_out_its_delay()
    {
        await using var host = For(async context =>
            await context.Step.RunAsync("rate-limited", () =>
                context.Attempt == 0 ? throw new RetryAfterException("slow down", TimeSpan.FromHours(1)) : "ok"));

        var run = await host.RunAsync("fn", Event(), Cancel);

        run.GetOutput<string>().ShouldBe("ok");
        run.GetStep("rate-limited").Errors.ShouldHaveSingleItem().ShouldBeOfType<RetryAfterException>();
    }

    [Fact]
    public async Task A_mocked_step_can_fail_on_some_attempts()
    {
        await using var host = For(async context => await context.Step.RunAsync("fetch", () => "real"));
        var mocks = new StepMocks().Run("fetch", attempt => attempt == 0 ? throw new TimeoutException() : "mocked on retry");

        var run = await host.RunAsync("fn", Event(), mocks, Cancel);

        run.GetOutput<string>().ShouldBe("mocked on retry");
        var step = run.GetStep("fetch");
        step.Attempts.ShouldBe(2);
        step.Mocked.ShouldBeTrue();
        step.Errors.ShouldHaveSingleItem().ShouldBeOfType<TimeoutException>();
    }

    [Fact]
    public async Task A_mock_that_throws_fails_the_step_on_every_attempt()
    {
        await using var host = For(async context => await context.Step.RunAsync("fetch", () => "real"), retries: 1);

        var run = await host.RunAsync("fn", Event(), new StepMocks().RunThrows("fetch", new TimeoutException("upstream timed out")), Cancel);

        run.Status.ShouldBe(TestRunStatus.Failed);
        run.GetStep("fetch").Attempts.ShouldBe(2);
        run.Error.ShouldBeOfType<StepFailedException>().Error.Message.ShouldBe("upstream timed out");
    }

    [Fact]
    public async Task A_non_retriable_step_error_fails_at_once()
    {
        await using var host = For(async context =>
            await context.Step.RunAsync<string>("validate", _ => throw new NonRetriableException("bad input")));

        var run = await host.RunAsync("fn", Event(), Cancel);

        run.Status.ShouldBe(TestRunStatus.Failed);
        run.GetStep("validate").Attempts.ShouldBe(1);
        run.GetStep("validate").Error.ShouldBeOfType<NonRetriableException>();
        run.Error.ShouldBeOfType<StepFailedException>().Error.Name.ShouldBe("NonRetriableException");
    }

    [Fact]
    public async Task A_function_error_is_retried_and_then_fails_the_run()
    {
        var calls = 0;
        await using var host = For(
            async context =>
            {
                calls++;
                await context.Step.RunAsync("a", () => 1);
                throw new InvalidOperationException("broken invariant");
            },
            retries: 1);

        var run = await host.RunAsync("fn", Event(), Cancel);

        run.Status.ShouldBe(TestRunStatus.Failed);
        run.Error.ShouldBeOfType<InvalidOperationException>().Message.ShouldBe("broken invariant");
        run.GetStep("a").Attempts.ShouldBe(1);

        // The first call ran the step; the next two failed, the first of them retried.
        calls.ShouldBe(3);
    }

    [Fact]
    public async Task A_non_retriable_function_error_fails_without_a_retry()
    {
        var calls = 0;
        await using var host = For(_ =>
        {
            calls++;
            throw new NonRetriableException("unsupported order");
        });

        var run = await host.RunAsync("fn", Event(), Cancel);

        run.Status.ShouldBe(TestRunStatus.Failed);
        run.Error.ShouldBeOfType<NonRetriableException>();
        calls.ShouldBe(1);
    }

    [Fact]
    public async Task A_step_failure_the_function_catches_lets_it_compensate()
    {
        await using var host = For(async context =>
        {
            try
            {
                await context.Step.RunAsync<string>("charge", _ => throw new NonRetriableException("card declined"));
                return "charged";
            }
            catch (StepFailedException failure)
            {
                await context.Step.RunAsync("refund-reservation", () => failure.Error.Message);
                return "compensated";
            }
        });

        var run = await host.RunAsync("fn", Event(), Cancel);

        run.GetOutput<string>().ShouldBe("compensated");
        run.Steps.Select(step => (step.Id, step.Status)).ShouldBe([("charge", TestStepStatus.Failed), ("refund-reservation", TestStepStatus.Completed)]);
        run.GetStep("refund-reservation").GetOutput<string>().ShouldBe("card declined");
    }

    [Fact]
    public async Task A_failure_handler_runs_once_the_function_has_failed_for_good()
    {
        var failures = new FailureLog();
        var services = new ServiceCollection();
        services.AddSingleton(failures);
        services.AddInngest(options => options.AppId = AppId)
            .AddFunction<ChargeCard>(new FunctionOptions { Id = "charge-card", Triggers = [Trigger.Event("order/placed")], Retries = 1 });
        await using var host = new InngestTestHost(services);

        var run = await host.RunAsync("charge-card", new InngestEvent("order/placed", new { orderId = "o_1" }), Cancel);

        run.Status.ShouldBe(TestRunStatus.Failed);
        var handler = run.FailureHandler.ShouldNotBeNull();
        handler.Status.ShouldBe(TestRunStatus.Completed);
        handler.FunctionId.ShouldBe("charge-card-failure");
        handler.GetStep("alert").Status.ShouldBe(TestStepStatus.Completed);

        var failure = failures.Failures.ShouldHaveSingleItem();
        failure.FunctionId.ShouldBe("test-app-charge-card");
        failure.RunId.ShouldBe(run.RunId);
        failure.Error.Name.ShouldBe("InvalidOperationException");
        failure.Error.Message.ShouldBe("card declined");
        failure.Event.GetProperty("name").GetString().ShouldBe("order/placed");
        failure.Event.GetProperty("data").GetProperty("orderId").GetString().ShouldBe("o_1");
    }

    [Fact]
    public async Task A_delegate_failure_handler_runs_too_and_not_for_a_run_that_completes()
    {
        var failed = new List<string>();
        var services = new ServiceCollection();
        services.AddInngest(options => options.AppId = AppId).AddFunction(
            Options(retries: 0),
            (context, _) => context.Event.Data.GetProperty("fail").GetBoolean()
                ? throw new InvalidOperationException("asked to fail")
                : Task.FromResult<object?>("ok"),
            onFailure: (context, _) =>
            {
                failed.Add(context.Event.Data.Error.Message);
                return Task.CompletedTask;
            });
        await using var host = new InngestTestHost(services);

        var completed = await host.RunAsync("fn", Event(new { fail = false }), Cancel);
        var failedRun = await host.RunAsync("fn", Event(new { fail = true }), Cancel);

        completed.FailureHandler.ShouldBeNull();
        failedRun.FailureHandler.ShouldNotBeNull().Status.ShouldBe(TestRunStatus.Completed);
        failed.ShouldBe(["asked to fail"]);
    }

    [Fact]
    public async Task A_function_that_cannot_be_resolved_fails_with_the_container_error()
    {
        var services = new ServiceCollection();
        services.AddInngest(options => options.AppId = AppId)
            .AddFunction<ChargeCard>(new FunctionOptions { Id = "charge-card", Triggers = [Trigger.Event("order/placed")], Retries = 0 });
        await using var host = new InngestTestHost(services);

        var run = await host.RunAsync("charge-card", new InngestEvent("order/placed"), Cancel);

        run.Status.ShouldBe(TestRunStatus.Failed);
        run.Error.ShouldBeOfType<InvalidOperationException>().Message.ShouldContain(nameof(FailureLog));
        run.Steps.ShouldBeEmpty();
    }

    [Fact]
    public async Task An_event_that_does_not_fit_the_event_type_fails_the_run_at_once()
    {
        var services = new ServiceCollection();
        services.AddInngest(options => options.AppId = AppId)
            .AddFunction<Amount>(Options(), (context, _) => Task.FromResult<object?>(context.Event.Data.Value));
        await using var host = new InngestTestHost(services);

        var run = await host.RunAsync("fn", Event(new { value = "not a number" }), Cancel);

        run.Status.ShouldBe(TestRunStatus.Failed);
        run.Error.ShouldBeOfType<InngestException>().Message.ShouldStartWith("JsonException: ");
    }

    public sealed record Amount(int Value);

    public sealed class FailureLog
    {
        public List<FunctionFailure> Failures { get; } = [];
    }

    public sealed class ChargeCard(FailureLog failures) : InngestFunction<JsonElement>, IFailureHandler
    {
        public override async Task<object?> ExecuteAsync(FunctionContext<JsonElement> context, CancellationToken cancellationToken)
        {
            await context.Step.RunAsync<string>("charge", _ => throw new InvalidOperationException("card declined"));
            return null;
        }

        public async Task OnFailureAsync(FunctionContext<FunctionFailure> context, CancellationToken cancellationToken)
        {
            await context.Step.RunAsync("alert", () => $"order failed: {context.Event.Data.Error.Message}");
            failures.Failures.Add(context.Event.Data);
        }
    }
}
