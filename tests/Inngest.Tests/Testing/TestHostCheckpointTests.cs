using Inngest.Testing;
using static Inngest.Tests.Testing.TestHosts;

namespace Inngest.Tests.Testing;

// RunUntilAsync stops a run at a checkpoint, as run.waitFor does in @inngest/test, and the send-event and publish steps
// take mocks.
public sealed class TestHostCheckpointTests
{
    private static readonly RealtimeTopic Status = new("order:7", "status");

    [Fact]
    public async Task A_run_stops_at_the_steps_found_for_a_parallel_group_before_any_of_them_runs()
    {
        var ran = new List<string>();
        await using var host = For(async context =>
        {
            var before = await context.Step.RunAsync("before", () => 1);
            var results = await Task.WhenAll(
                context.Step.RunAsync("a", () => Ran(ran, "a")),
                context.Step.RunAsync("b", () => Ran(ran, "b")));
            return before + results.Sum();
        });

        var run = await host.RunUntilAsync("fn", Event(), TestCheckpoint.StepsFound("a", "b"), Cancel);

        run.Status.ShouldBe(TestRunStatus.Stopped);
        run.StoppedAt.Select(step => (step.Id, step.Op, step.Status)).ShouldBe([
            ("a", "StepPlanned", TestStepStatus.Pending),
            ("b", "StepPlanned", TestStepStatus.Pending),
        ]);
        run.GetStep("before").Status.ShouldBe(TestStepStatus.Completed);
        ran.ShouldBeEmpty();
        run.ToString().ShouldBe("Stopped at step \"a\" (StepPlanned), step \"b\" (StepPlanned).");
    }

    [Fact]
    public async Task Steps_found_without_ids_stops_at_the_first_step_the_function_does_not_run_itself()
    {
        await using var host = For(async context =>
        {
            await context.Step.RunAsync("prepare", () => 1);
            await context.Step.SleepAsync("cool-off", TimeSpan.FromMinutes(5));
            return "done";
        });

        var run = await host.RunUntilAsync("fn", Event(), TestCheckpoint.StepsFound(), new StepMocks().AllSleeps(), Cancel);

        run.Status.ShouldBe(TestRunStatus.Stopped);
        var sleep = run.StoppedAt.ShouldHaveSingleItem();
        (sleep.Id, sleep.Type, sleep.Status).ShouldBe(("cool-off", StepType.Sleep, TestStepStatus.Pending));
        sleep.Options.GetProperty("duration").GetString().ShouldBe("5m");
    }

    [Fact]
    public async Task A_run_stops_after_the_first_attempt_of_a_step_before_it_is_retried()
    {
        var attempts = 0;
        await using var host = For(async context => await context.Step.RunAsync("charge", () =>
        {
            attempts++;
            return attempts == 1 ? throw new InvalidOperationException("card declined") : "charged";
        }));

        var run = await host.RunUntilAsync("fn", Event(), TestCheckpoint.StepRan("charge"), Cancel);

        run.Status.ShouldBe(TestRunStatus.Stopped);
        var step = run.StoppedAt.ShouldHaveSingleItem();
        (step.Id, step.Status, step.Attempts).ShouldBe(("charge", TestStepStatus.Pending, 1));
        step.Errors.ShouldHaveSingleItem().Message.ShouldBe("card declined");
        attempts.ShouldBe(1);
    }

    [Fact]
    public async Task A_run_that_completes_before_the_checkpoint_ends_as_completed()
    {
        await using var host = For(async context => await context.Step.RunAsync("only", () => "done"));

        var run = await host.RunUntilAsync("fn", Event(), TestCheckpoint.StepsFound("never"), Cancel);

        run.Status.ShouldBe(TestRunStatus.Completed);
        run.StoppedAt.ShouldBeEmpty();
    }

    [Fact]
    public async Task ExecuteStepAsync_reports_the_step_it_stopped_at()
    {
        await using var host = For(async context =>
        {
            await context.Step.RunAsync("a", () => 1);
            return await context.Step.RunAsync("b", () => 2);
        });

        var run = await host.ExecuteStepAsync("fn", "a", Event(), Cancel);

        run.StoppedAt.ShouldHaveSingleItem().Id.ShouldBe("a");
    }

    [Fact]
    public async Task A_mocked_send_event_step_returns_the_chosen_ids_and_sends_nothing()
    {
        await using var host = For(async context => (await context.Step.SendEventAsync("notify", new InngestEvent("order/shipped", new { id = 7 }))).Ids);

        var run = await host.RunAsync("fn", Event(), new StepMocks().SendEvent("notify", "evt-1"), Cancel);

        run.Status.ShouldBe(TestRunStatus.Completed);
        run.GetOutput<string[]>().ShouldBe(["evt-1"]);
        run.GetStep("notify").Mocked.ShouldBeTrue();
        run.SentEvents.ShouldBeEmpty();
        host.SentEvents.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_send_event_step_that_fails_once_is_retried()
    {
        await using var host = For(async context => (await context.Step.SendEventAsync("notify", new InngestEvent("order/shipped", null))).Ids[0]);
        var mocks = new StepMocks().SendEvent("notify", attempt => attempt == 0 ? throw new HttpRequestException("Inngest is down") : ["evt-2"]);

        var run = await host.RunAsync("fn", Event(), mocks, Cancel);

        run.Status.ShouldBe(TestRunStatus.Completed);
        run.GetOutput<string>().ShouldBe("evt-2");
        var step = run.GetStep("notify");
        step.Attempts.ShouldBe(2);
        step.Errors.ShouldHaveSingleItem().ShouldBeOfType<HttpRequestException>();
    }

    [Fact]
    public async Task A_send_event_step_that_keeps_failing_fails_the_step_for_good()
    {
        await using var host = For(async context =>
        {
            try
            {
                await context.Step.SendEventAsync("notify", new InngestEvent("order/shipped", null));
                return "sent";
            }
            catch (StepFailedException exception)
            {
                return exception.Error.Message;
            }
        });

        var run = await host.RunAsync("fn", Event(), new StepMocks().SendEventThrows("notify", new NonRetriableException("event key revoked")), Cancel);

        run.GetOutput<string>().ShouldBe("event key revoked");
        run.GetStep("notify").Status.ShouldBe(TestStepStatus.Failed);
        run.SentEvents.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_publish_step_can_be_made_to_fail()
    {
        await using var host = For(async context =>
        {
            try
            {
                await context.Step.PublishAsync("status", Status, "packed");
                return "published";
            }
            catch (StepFailedException exception)
            {
                return exception.Error.Message;
            }
        });

        var run = await host.RunAsync("fn", Event(), new StepMocks().PublishThrows("status", new NonRetriableException("realtime is off")), Cancel);

        run.GetOutput<string>().ShouldBe("realtime is off");
        run.GetStep("status").Type.ShouldBe(StepType.RealtimePublish);
        run.Published.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_send_event_mock_on_a_run_step_says_the_kinds_do_not_match()
    {
        await using var host = For(async context => await context.Step.RunAsync("notify", () => "ran"));

        var exception = await Should.ThrowAsync<InvalidOperationException>(() => host.RunAsync("fn", Event(), new StepMocks().SendEvent("notify", "evt-1"), Cancel));

        exception.Message.ShouldBe("The mock for step \"notify\" answers a send-event step, but the step is a run step.");
    }

    [Fact]
    public void Checkpoints_check_their_ids()
    {
        Should.Throw<ArgumentException>(() => TestCheckpoint.StepRan(""));
        Should.Throw<ArgumentException>(() => TestCheckpoint.StepsFound("a", ""));
        TestCheckpoint.StepsFound("a", "b").ToString().ShouldBe("steps \"a\", \"b\" found");
    }

    private static int Ran(List<string> ran, string id)
    {
        lock (ran)
        {
            ran.Add(id);
        }

        return 1;
    }
}
