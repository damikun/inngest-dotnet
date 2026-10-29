using Inngest.Testing;
using static Inngest.Tests.Testing.TestHosts;

namespace Inngest.Tests.Testing;

// The gateway steps of the real engine: the host types the ones it cannot carry out and mocks a sent signal.
public sealed class TestHostGatewayStepTests
{
    [Fact]
    public async Task A_run_waiting_for_a_signal_says_so()
    {
        await using var host = For(async context =>
            await context.Step.WaitForSignalAsync("approval", new WaitForSignalOptions("approval/order-1", TimeSpan.FromHours(1))));

        var run = await host.RunAsync("fn", Event(), Cancel);

        run.Status.ShouldBe(TestRunStatus.Waiting);
        var step = run.WaitingOn.ShouldHaveSingleItem();
        step.Id.ShouldBe("approval");
        step.Type.ShouldBe(StepType.WaitForSignal);
    }

    [Fact]
    public async Task Fetch_and_inference_steps_are_typed_while_the_run_waits_on_them()
    {
        await using var host = For(async context =>
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.example.com/users/1");
            var fetch = context.Step.FetchAsync("user", request);
            var infer = context.Step.InferAsync("summary", new InferOptions(AIFormat.OpenAIChat, new Uri("https://api.openai.com/v1/chat/completions"), new { model = "gpt-4o" }));
            await Task.WhenAll(fetch, infer);
            return null;
        });

        var run = await host.RunAsync("fn", Event(), Cancel);

        run.Status.ShouldBe(TestRunStatus.Waiting);
        run.WaitingOn.Select(step => (step.Id, step.Type)).ShouldBe([("user", StepType.Fetch), ("summary", StepType.AIInfer)], ignoreOrder: true);
    }

    [Fact]
    public async Task A_send_signal_step_is_mocked_like_a_run_step()
    {
        await using var host = For(async context => await context.Step.SendSignalAsync("approve", new InngestSignal("approval/order-1")));

        var run = await host.RunAsync("fn", Event(), new StepMocks().Run<string?>("approve", "01JRESUMED"), Cancel);

        run.Status.ShouldBe(TestRunStatus.Completed);
        run.GetOutput<string>().ShouldBe("01JRESUMED");
        run.GetStep("approve").Mocked.ShouldBeTrue();
    }
}
