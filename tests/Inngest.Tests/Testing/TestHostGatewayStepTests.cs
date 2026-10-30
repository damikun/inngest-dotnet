using System.Net;
using System.Text.Json;
using Inngest.Testing;
using Microsoft.Extensions.DependencyInjection;
using static Inngest.Tests.Testing.TestHosts;

namespace Inngest.Tests.Testing;

// The signal, gateway and AI steps of the real engine: typed while a run waits on them, answered by their mocks.
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

    [Fact]
    public async Task A_signal_mock_resumes_the_wait_with_its_data()
    {
        await using var host = For(async context =>
            await context.Step.WaitForSignalAsync<Approval>("approval", new WaitForSignalOptions("approval/order-1", TimeSpan.FromHours(1))));

        var run = await host.RunAsync("fn", Event(), new StepMocks().WaitForSignal("approval", new Approval(true, "dana")), Cancel);

        run.Status.ShouldBe(TestRunStatus.Completed);
        run.GetOutput<InngestSignal<Approval>>().ShouldBe(new InngestSignal<Approval>("approval/order-1", new Approval(true, "dana")));
        run.GetStep("approval").Mocked.ShouldBeTrue();
    }

    [Fact]
    public async Task A_signal_mock_can_time_the_wait_out()
    {
        await using var host = For(async context =>
        {
            var signal = await context.Step.WaitForSignalAsync("approval", new WaitForSignalOptions("approval/order-1", TimeSpan.FromHours(1)));
            return signal is null ? "timed out" : "approved";
        });

        var run = await host.RunAsync("fn", Event(), new StepMocks().WaitForSignalTimeout("approval"), Cancel);

        run.Status.ShouldBe(TestRunStatus.Completed);
        run.GetOutput<string>().ShouldBe("timed out");
    }

    [Fact]
    public async Task A_fetch_mock_answers_the_request_as_Inngest_would()
    {
        await using var host = For(async context =>
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.example.com/users/1");
            using var response = await context.Step.FetchAsync("user", request);
            return $"{(int)response.StatusCode} {response.Headers.GetValues("X-Request-Id").Single()} {await response.Content.ReadAsStringAsync()}";
        });

        var mocks = new StepMocks().Fetch("user", HttpStatusCode.Accepted, """{"name":"Dana"}""", new Dictionary<string, string> { ["X-Request-Id"] = "r-1" });
        var run = await host.RunAsync("fn", Event(), mocks, Cancel);

        run.Status.ShouldBe(TestRunStatus.Completed);
        run.GetOutput<string>().ShouldBe("""202 r-1 {"name":"Dana"}""");
        run.GetStep("user").Output!.Value.GetProperty("status_code").GetInt32().ShouldBe(202);
    }

    [Fact]
    public async Task An_inference_mock_answers_with_the_provider_response()
    {
        await using var host = For(async context =>
        {
            var reply = await context.Step.InferAsync<Completion>(
                "summary",
                new InferOptions(AIFormat.OpenAIChat, new Uri("https://api.openai.com/v1/chat/completions"), new { model = "gpt-4o" }));
            return reply.Content;
        });

        var run = await host.RunAsync("fn", Event(), new StepMocks().Infer("summary", new Completion("Short and sweet")), Cancel);

        run.Status.ShouldBe(TestRunStatus.Completed);
        run.GetOutput<string>().ShouldBe("Short and sweet");
        run.GetStep("summary").Type.ShouldBe(StepType.AIInfer);
    }

    [Fact]
    public async Task An_AI_wrap_step_is_mocked_like_a_run_step()
    {
        var called = false;
        await using var host = For(async context => await context.Step.AIWrapAsync("draft", "Write a haiku", prompt =>
        {
            called = true;
            return Task.FromResult(prompt);
        }));

        var run = await host.RunAsync("fn", Event(), new StepMocks().Run("draft", "An old silent pond"), Cancel);

        run.Status.ShouldBe(TestRunStatus.Completed);
        run.GetOutput<string>().ShouldBe("An old silent pond");
        var step = run.GetStep("draft");
        step.Type.ShouldBe(StepType.AIWrap);
        step.Mocked.ShouldBeTrue();
        step.Options.GetProperty("input")[0].GetString().ShouldBe("Write a haiku");
        called.ShouldBeFalse();
    }

    [Fact]
    public async Task An_unmocked_AI_wrap_step_runs_its_handler()
    {
        await using var host = For(async context => await context.Step.AIWrapAsync("draft", "haiku", prompt => Task.FromResult($"A {prompt}")));

        var run = await host.RunAsync("fn", Event(), Cancel);

        run.Status.ShouldBe(TestRunStatus.Completed);
        run.GetOutput<string>().ShouldBe("A haiku");
    }

    [Fact]
    public async Task A_signal_that_is_not_mocked_resumes_no_run_and_is_recorded()
    {
        await using var host = For(async context =>
        {
            var resumed = await context.Step.SendSignalAsync("approve", new InngestSignal("approval/order-1", new Approval(true, "dana")));
            await context.Step.RunAsync("notify", async () =>
                await context.Services.GetRequiredService<IInngestClient>().SendSignalAsync(new InngestSignal("approval/order-2")));
            return resumed ?? "no run waited";
        });

        var run = await host.RunAsync("fn", Event(), Cancel);

        run.Status.ShouldBe(TestRunStatus.Completed);
        run.GetOutput<string>().ShouldBe("no run waited");
        run.GetStep("approve").Type.ShouldBe(StepType.SendSignal);
        run.SentSignals.Select(signal => signal.Name).ShouldBe(["approval/order-1", "approval/order-2"]);
        run.SentSignals[0].Data.GetProperty("approved").GetBoolean().ShouldBeTrue();
        run.SentSignals[1].Data.ValueKind.ShouldBe(JsonValueKind.Null);
        host.SentSignals.Count.ShouldBe(2);
    }

    [Fact]
    public async Task A_send_signal_mock_says_which_run_it_resumed()
    {
        await using var host = For(async context => await context.Step.SendSignalAsync("approve", new InngestSignal("approval/order-1")));

        var run = await host.RunAsync("fn", Event(), new StepMocks().SendSignal("approve", "01JRESUMED"), Cancel);

        run.GetOutput<string>().ShouldBe("01JRESUMED");
        run.SentSignals.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_mock_of_another_type_is_refused()
    {
        await using var host = For(async context =>
            await context.Step.WaitForSignalAsync("approval", new WaitForSignalOptions("approval/order-1", TimeSpan.FromHours(1))));

        var exception = await Should.ThrowAsync<InvalidOperationException>(() =>
            host.RunAsync("fn", Event(), new StepMocks().Fetch("approval", HttpStatusCode.OK), Cancel));

        exception.Message.ShouldBe("The mock for step \"approval\" answers a fetch, but the step is a wait for a signal.");
    }

    private sealed record Approval(bool Approved, string By);

    private sealed record Completion(string Content);
}
