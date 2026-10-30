using System.Text.Json;
using Inngest.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using static Inngest.Tests.Testing.TestHosts;

namespace Inngest.Tests.Testing;

// Sleeps and waits are carried out by Inngest, so the host answers them from mocks, at the simulated time they
// would complete at, and leaves the run waiting on any without one.
public sealed class TestHostTimeTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 29, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task A_sleep_without_a_mock_leaves_the_run_waiting()
    {
        var after = 0;
        await using var host = For(async context =>
        {
            await context.Step.SleepAsync("cool-down", TimeSpan.FromHours(1));
            await context.Step.RunAsync("after", () => ++after);
            return null;
        });

        var run = await host.RunAsync("fn", Event(), Cancel);

        run.Status.ShouldBe(TestRunStatus.Waiting);
        var sleep = run.WaitingOn.ShouldHaveSingleItem();
        sleep.Id.ShouldBe("cool-down");
        sleep.Type.ShouldBe(StepType.Sleep);
        sleep.Status.ShouldBe(TestStepStatus.Pending);
        sleep.Options.GetProperty("duration").GetString().ShouldBe("1h");
        run.Steps.ShouldHaveSingleItem();
        after.ShouldBe(0);
    }

    [Fact]
    public async Task A_mocked_sleep_ends_and_the_run_goes_on()
    {
        await using var host = For(async context =>
        {
            await context.Step.SleepAsync("cool-down", TimeSpan.FromHours(1));
            return await context.Step.RunAsync("after", () => "done");
        });

        var run = await host.RunAsync("fn", Event(), new StepMocks().Sleep("cool-down"), Cancel);

        run.GetOutput<string>().ShouldBe("done");
        var sleep = run.GetStep("cool-down");
        sleep.Status.ShouldBe(TestStepStatus.Completed);
        sleep.Mocked.ShouldBeTrue();
        sleep.Output!.Value.ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task All_sleeps_can_end_at_once()
    {
        await using var host = For(async context =>
        {
            for (var i = 0; i < 3; i++)
            {
                await context.Step.SleepAsync("poll-interval", TimeSpan.FromMinutes(10));
            }

            return "polled";
        });

        var run = await host.RunAsync("fn", Event(), new StepMocks().AllSleeps(), Cancel);

        run.GetOutput<string>().ShouldBe("polled");
        run.Steps.Select(step => step.Id).ShouldBe(["poll-interval", "poll-interval:1", "poll-interval:2"]);
    }

    [Fact]
    public async Task A_fake_clock_moves_to_the_end_of_each_sleep()
    {
        var time = new FakeTimeProvider(Start);
        await using var host = For(
            async context =>
            {
                var clock = context.Services.GetRequiredService<TimeProvider>();
                await context.Step.SleepAsync("short", TimeSpan.FromHours(2));
                var afterShort = await context.Step.RunAsync("after-short", () => clock.GetUtcNow());
                await context.Step.SleepUntilAsync("until", Start.AddDays(1));
                var afterUntil = await context.Step.RunAsync("after-until", () => clock.GetUtcNow());
                return new[] { afterShort, afterUntil };
            },
            configure: inngest => inngest.Services.AddSingleton<TimeProvider>(time));

        var run = await host.RunAsync("fn", Event(), new StepMocks().AllSleeps(), Cancel);

        run.GetOutput<DateTimeOffset[]>().ShouldBe([Start.AddHours(2), Start.AddDays(1)]);
        time.GetUtcNow().ShouldBe(Start.AddDays(1));
    }

    [Fact]
    public async Task Parallel_sleeps_complete_in_the_order_of_their_end()
    {
        await using var host = For(async context =>
        {
            var hour = context.Step.SleepAsync("hour", TimeSpan.FromHours(1));
            var minute = context.Step.SleepAsync("minute", TimeSpan.FromMinutes(1));
            return await Task.WhenAny(hour, minute) == minute ? "minute" : "hour";
        });

        var run = await host.RunAsync("fn", Event(), new StepMocks().AllSleeps(), Cancel);

        run.GetOutput<string>().ShouldBe("minute");
    }

    [Fact]
    public async Task A_wait_receives_the_mocked_event()
    {
        await using var host = For(async context =>
        {
            var approval = await context.Step.WaitForEventAsync<Approval>(
                "wait-for-approval",
                new WaitForEventOptions("order/approved", TimeSpan.FromDays(1)) { Match = "data.orderId" });
            return approval is null ? "timed out" : $"approved by {approval.Data.By}";
        });
        var approved = new InngestEvent("order/approved", new Approval("o_1", "ada"));

        var run = await host.RunAsync("fn", Event(new { orderId = "o_1" }), new StepMocks().WaitForEvent("wait-for-approval", approved), Cancel);

        run.GetOutput<string>().ShouldBe("approved by ada");
        var wait = run.GetStep("wait-for-approval");
        wait.Type.ShouldBe(StepType.WaitForEvent);
        wait.Output!.Value.GetProperty("name").GetString().ShouldBe("order/approved");
        wait.Options.GetProperty("event").GetString().ShouldBe("order/approved");
        wait.Options.GetProperty("timeout").GetString().ShouldBe("1d");
        wait.Options.GetProperty("if").GetString().ShouldBe("event.data.orderId == async.data.orderId");
    }

    [Fact]
    public async Task A_wait_can_time_out()
    {
        await using var host = For(async context =>
        {
            var approval = await context.Step.WaitForEventAsync("wait-for-approval", new WaitForEventOptions("order/approved", TimeSpan.FromDays(1)));
            return approval is null ? "timed out" : "approved";
        });

        var run = await host.RunAsync("fn", Event(), new StepMocks().WaitForEventTimeout("wait-for-approval"), Cancel);

        run.GetOutput<string>().ShouldBe("timed out");
        run.GetStep("wait-for-approval").Output!.Value.ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task A_wait_without_a_mock_leaves_the_run_waiting()
    {
        await using var host = For(async context =>
            await context.Step.WaitForEventAsync("wait-for-approval", new WaitForEventOptions("order/approved", TimeSpan.FromDays(1))));

        var run = await host.RunAsync("fn", Event(), Cancel);

        run.Status.ShouldBe(TestRunStatus.Waiting);
        run.WaitingOn.ShouldHaveSingleItem().Id.ShouldBe("wait-for-approval");
    }

    [Fact]
    public async Task A_timeout_completes_after_a_shorter_sleep()
    {
        await using var host = For(async context =>
        {
            var approval = context.Step.WaitForEventAsync("approval", new WaitForEventOptions("order/approved", TimeSpan.FromDays(1)));
            var reminder = context.Step.SleepAsync("reminder", TimeSpan.FromHours(1));
            return await Task.WhenAny(approval, reminder) == reminder ? "reminder" : "approval";
        });

        var timedOut = await host.RunAsync("fn", Event(), new StepMocks().WaitForEventTimeout("approval").Sleep("reminder"), Cancel);
        var received = await host.RunAsync(
            "fn",
            Event(),
            new StepMocks().WaitForEvent("approval", new InngestEvent("order/approved")).Sleep("reminder"),
            Cancel);

        timedOut.GetOutput<string>().ShouldBe("reminder");
        received.GetOutput<string>().ShouldBe("approval");
    }

    [Fact]
    public async Task In_a_parallel_group_a_wait_without_a_mock_holds_up_the_run()
    {
        await using var host = For(async context =>
        {
            var approved = context.Step.WaitForEventAsync("approved", new WaitForEventOptions("order/approved", TimeSpan.FromDays(1)));
            var rejected = context.Step.WaitForEventAsync("rejected", new WaitForEventOptions("order/rejected", TimeSpan.FromDays(1)));
            return await Task.WhenAny(approved, rejected) == approved ? "approved" : "rejected";
        });

        var run = await host.RunAsync("fn", Event(), new StepMocks().WaitForEvent("approved", new InngestEvent("order/approved")), Cancel);

        // Without race mode Inngest resumes the run only once every parallel step is done.
        run.Status.ShouldBe(TestRunStatus.Waiting);
        run.GetStep("approved").Status.ShouldBe(TestStepStatus.Completed);
        run.WaitingOn.ShouldHaveSingleItem().Id.ShouldBe("rejected");
    }

    [Fact]
    public async Task A_mocked_event_must_have_the_name_the_step_waits_for()
    {
        await using var host = For(async context =>
            await context.Step.WaitForEventAsync("wait-for-approval", new WaitForEventOptions("order/approved", TimeSpan.FromDays(1))));

        var exception = await Should.ThrowAsync<InvalidOperationException>(() =>
            host.RunAsync("fn", Event(), new StepMocks().WaitForEvent("wait-for-approval", new InngestEvent("order/rejected")), Cancel));

        exception.Message.ShouldBe("Step \"wait-for-approval\" waits for \"order/approved\", but its mock receives \"order/rejected\".");
    }

    [Fact]
    public async Task A_mock_must_fit_its_step()
    {
        await using var host = For(async context =>
        {
            await context.Step.SleepAsync("cool-down", TimeSpan.FromHours(1));
            return null;
        });

        var exception = await Should.ThrowAsync<InvalidOperationException>(() =>
            host.RunAsync("fn", Event(), new StepMocks().Run("cool-down", "value"), Cancel));

        exception.Message.ShouldBe("The mock for step \"cool-down\" answers a run step, but the step is a sleep.");
    }

    // Otherwise a mismatch would name the step type as it is spelled in C#.
    [Fact]
    public void Every_step_type_has_a_description_for_mock_errors() =>
        Enum.GetValues<StepType>().ShouldAllBe(type => RunDriver.Describe(type) != type.ToString());

    public sealed record Approval(string OrderId, string By);
}
