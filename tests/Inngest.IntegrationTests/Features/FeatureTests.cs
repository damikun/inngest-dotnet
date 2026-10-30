using Inngest.IntegrationTests.Evaluation.Functions;
using Inngest.IntegrationTests.Evaluation.Harness;
using Inngest.IntegrationTests.Harness;
using Microsoft.Extensions.DependencyInjection;

namespace Inngest.IntegrationTests.Features;

public sealed class FeatureTests(EvaluationEnvironment environment) : EvaluationScenario<FeaturesFixture>(environment)
{
    [Fact(Timeout = 90_000)]
    public async Task A_call_longer_than_three_seconds_is_streamed_and_Inngest_reads_its_result()
    {
        var run = await RunToEndAsync(FeatureFunctions.LongStep, new RunEvent(TestId));

        run.Status.ShouldBe(RunStatus.Completed);
        run.OutputAs<string>().ShouldBe("finished after a long step");
        Fixture.App.Requests.Snapshot()
            .Where(request => request.Method == "POST" && request.Target.Contains("stream-long-step", StringComparison.Ordinal))
            .Select(request => request.StatusCode)
            .ShouldContain(201);
    }

    [Fact(Timeout = 60_000)]
    public async Task An_AI_wrap_step_runs_and_its_result_is_the_output()
    {
        var run = await RunToEndAsync(FeatureFunctions.Wrapped, new RunEvent(TestId));

        run.Status.ShouldBe(RunStatus.Completed);
        run.OutputAs<string>().ShouldBe("A haiku");
    }

    [Fact(Timeout = 60_000)]
    public async Task The_dev_server_accepts_a_published_message_and_mints_a_subscription_token()
    {
        var run = await RunToEndAsync(FeatureFunctions.Published, new RunEvent(TestId));

        run.Status.ShouldBe(RunStatus.Completed);
        run.OutputAs<string>().ShouldBe("Analyzing");
        var token = await Fixture.App.Services.GetRequiredService<IInngestRealtime>()
            .GetSubscriptionTokenAsync($"run:{TestId}", ["status"], Cancellation);
        token.Key.Split('.').Length.ShouldBe(3);
        token.Topics.ShouldBe(["status"]);
    }

    [Fact(Timeout = 60_000)]
    public async Task A_run_passes_the_sessions_of_its_event_on_to_the_events_it_sends()
    {
        var client = Fixture.App.Services.GetRequiredService<IInngestClient>();

        await client.SendAsync(new InngestEvent(FeatureFunctions.SessionsParent, new RunEvent(TestId))
        {
            Meta = new EventMeta { Sessions = new Dictionary<string, string?> { ["ticket_id"] = "t-1", ["user_id"] = "u-1" } },
        });

        var child = await Observed.WaitForAsync("child", TimeSpan.FromSeconds(30), Cancellation);
        // The parent records on each call: before its step and after it.
        Observed.Values<string>("parent").Distinct().ShouldBe(["ticket_id=t-1,user_id=u-1"]);
        child.Value.ShouldBe("ticket_id=t-1,user_id=u-1");
    }
}
