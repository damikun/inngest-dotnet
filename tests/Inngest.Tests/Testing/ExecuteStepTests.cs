using Inngest.Testing;
using static Inngest.Tests.Testing.TestHosts;

namespace Inngest.Tests.Testing;

// ExecuteStepAsync runs a function only as far as one step, as executeStep does in @inngest/test.
public sealed class ExecuteStepTests
{
    [Fact]
    public async Task The_run_stops_once_the_step_has_run()
    {
        var ran = new List<string>();
        await using var host = For(async context =>
        {
            var a = await context.Step.RunAsync("a", () =>
            {
                ran.Add("a");
                return "result-a";
            });
            return await context.Step.RunAsync("b", () =>
            {
                ran.Add("b");
                return a + "-b";
            });
        });

        var run = await host.ExecuteStepAsync("fn", "a", Event(), Cancel);

        run.Status.ShouldBe(TestRunStatus.Stopped);
        run.GetStep("a").GetOutput<string>().ShouldBe("result-a");
        run.Steps.ShouldHaveSingleItem();
        ran.ShouldBe(["a"]);
        run.ToString().ShouldBe("Stopped after step \"a\" (StepRun).");
    }

    [Fact]
    public async Task A_step_can_run_on_a_mocked_earlier_step()
    {
        await using var host = For(async context =>
        {
            var a = await context.Step.RunAsync("a", () => "real-a");
            var b = await context.Step.RunAsync("b", () => $"from-{a}");
            return await context.Step.RunAsync("c", () => b + "-c");
        });

        var run = await host.ExecuteStepAsync("fn", "b", Event(), new StepMocks().Run("a", "mocked-a"), Cancel);

        run.Status.ShouldBe(TestRunStatus.Stopped);
        run.GetStep("b").GetOutput<string>().ShouldBe("from-mocked-a");
        run.Steps.Select(step => step.Id).ShouldBe(["a", "b"]);
    }

    [Fact]
    public async Task A_sleep_or_wait_stops_the_run_where_it_is_reached()
    {
        await using var host = For(async context =>
        {
            await context.Step.SleepAsync("wait-a-day", TimeSpan.FromDays(1));
            await context.Step.WaitForEventAsync("wait-for-approval", new WaitForEventOptions("app/approved", TimeSpan.FromHours(1)));
            return "finished";
        });

        var sleep = await host.ExecuteStepAsync("fn", "wait-a-day", Event(), Cancel);
        var wait = await host.ExecuteStepAsync("fn", "wait-for-approval", Event(), new StepMocks().Sleep("wait-a-day"), Cancel);

        sleep.Status.ShouldBe(TestRunStatus.Stopped);
        sleep.GetStep("wait-a-day").Status.ShouldBe(TestStepStatus.Pending);
        sleep.GetStep("wait-a-day").Options.GetProperty("duration").GetString().ShouldBe("1d");

        wait.Status.ShouldBe(TestRunStatus.Stopped);
        var step = wait.GetStep("wait-for-approval");
        step.Status.ShouldBe(TestStepStatus.Pending);
        step.Options.GetProperty("event").GetString().ShouldBe("app/approved");
        step.Options.GetProperty("timeout").GetString().ShouldBe("1h");
    }

    [Fact]
    public async Task A_failing_step_is_retried_before_the_run_stops()
    {
        await using var host = For(
            async context =>
            {
                await context.Step.RunAsync<string>("charge", _ => throw new InvalidOperationException("card declined"));
                return null;
            },
            retries: 1);

        var run = await host.ExecuteStepAsync("fn", "charge", Event(), Cancel);

        run.Status.ShouldBe(TestRunStatus.Stopped);
        var step = run.GetStep("charge");
        step.Status.ShouldBe(TestStepStatus.Failed);
        step.Attempts.ShouldBe(2);
        step.Error.ShouldBeOfType<InvalidOperationException>();
    }

    [Fact]
    public async Task A_planned_step_stops_the_run_once_it_has_run()
    {
        await using var host = For(async context =>
        {
            var results = await Task.WhenAll(context.Step.RunAsync("a", () => "A"), context.Step.RunAsync("b", () => "B"));
            return string.Concat(results);
        });

        var run = await host.ExecuteStepAsync("fn", "b", Event(), Cancel);

        run.Status.ShouldBe(TestRunStatus.Stopped);
        run.GetStep("b").GetOutput<string>().ShouldBe("B");
    }

    [Fact]
    public async Task A_run_that_never_reaches_the_step_ends_as_it_would()
    {
        await using var host = For(async context => await context.Step.RunAsync("a", () => 1));

        var run = await host.ExecuteStepAsync("fn", "missing", Event(), Cancel);

        run.Status.ShouldBe(TestRunStatus.Completed);
        Should.Throw<KeyNotFoundException>(() => run.GetStep("missing")).Message
            .ShouldBe("The run did not reach a step \"missing\". Steps it reached: \"a\".");
    }
}
