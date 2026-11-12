using System.Text.Json;
using Inngest.Testing;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using static Inngest.Tests.Testing.TestHosts;

namespace Inngest.Extensions.AI.Tests;

// A Microsoft Agent Framework agent made from a chat client whose pipeline has UseInngestFunctionInvocation and
// UseInngestSteps: ChatClientAgent finds the function invoking client of the pipeline and adds no other, so its model calls
// and its tool calls are steps of the run, and the agent runs durably with nothing of its own changed.
public sealed class AgentFrameworkTests
{
    [Fact]
    public async Task An_agent_calls_the_model_and_its_tools_once_across_the_replays_of_its_run()
    {
        var invocations = 0;
        var model = new FakeChatClient((call, _) => call == 0 ? Calls(Call("call_1", "get_weather", "Prague")) : Says("It is sunny in Prague."));
        var agent = Pipeline(model).AsAIAgent(instructions: "You answer questions about the weather.", name: "forecaster", tools: [Weather(() => invocations++)]);
        var services = Services(async context =>
        {
            var reply = await agent.RunAsync("What is the weather in Prague?");
            await context.Step.SleepAsync("pause", TimeSpan.FromMinutes(5));
            return reply.Text;
        });
        await using var host = new InngestTestHost(services.AddFakeLogging());

        var run = await host.RunAsync("fn", Event(), new StepMocks().Sleep("pause"), Cancel);

        run.Status.ShouldBe(TestRunStatus.Completed);
        run.GetOutput<string>().ShouldBe("It is sunny in Prague.");
        run.Steps.Select(step => (step.Id, step.Type)).ShouldBe(
        [
            ("chat", StepType.AIWrap),
            ("tool get_weather", StepType.AIWrap),
            ("chat:1", StepType.AIWrap),
            ("pause", StepType.Sleep),
        ]);
        run.Steps.ShouldAllBe(step => step.Attempts <= 1);
        (invocations, model.CallCount).ShouldBe((1, 2));

        // The agent's instructions reached the model, and the replays asked for each step with the input it was saved with.
        model.Options[0]!.Instructions.ShouldBe("You answer questions about the weather.");
        host.Services.GetFakeLogCollector().GetSnapshot().ShouldNotContain(record => record.Level >= Microsoft.Extensions.Logging.LogLevel.Warning);
    }

    [Fact]
    public async Task A_tool_call_of_an_agent_is_a_step_with_the_arguments_as_input_and_the_result_as_output()
    {
        var model = new FakeChatClient((call, _) => call == 0 ? Calls(Call("call_1", "get_weather", "Prague")) : Says("Done."));
        var agent = Pipeline(model).AsAIAgent(tools: [Weather(() => { })]);
        await using var host = For(async context => (await agent.RunAsync("Weather?")).Text);

        var run = await host.RunAsync("fn", Event(), Cancel);

        var step = run.GetStep("tool get_weather");
        step.Options.GetProperty("type").GetString().ShouldBe("step.ai.wrap");
        var input = step.Options.GetProperty("input").EnumerateArray().ShouldHaveSingleItem();
        input.GetProperty("name").GetString().ShouldBe("get_weather");
        input.GetProperty("arguments").GetProperty("city").GetString().ShouldBe("Prague");
        step.Output!.Value.GetProperty("result").GetString().ShouldBe("Sunny in Prague");

        // The model got the result that the step saved.
        var given = model.Messages[1].SelectMany(message => message.Contents).OfType<FunctionResultContent>().Single();
        ((JsonElement)given.Result!).GetString().ShouldBe("Sunny in Prague");
    }

    [Fact]
    public async Task A_streaming_agent_run_makes_the_same_steps()
    {
        var invocations = 0;
        var model = new FakeChatClient((call, _) => call == 0 ? Calls(Call("call_1", "get_weather", "Prague")) : Says("It is sunny in Prague."));
        var agent = Pipeline(model).AsAIAgent(tools: [Weather(() => invocations++)]);
        await using var host = For(async context =>
        {
            var reply = await agent.RunStreamingAsync("Weather?").ToAgentResponseAsync();
            await context.Step.SleepAsync("pause", TimeSpan.FromMinutes(1));
            return reply.Text;
        });

        var run = await host.RunAsync("fn", Event(), new StepMocks().Sleep("pause"), Cancel);

        run.GetOutput<string>().ShouldBe("It is sunny in Prague.");
        run.Steps.Select(step => step.Id).ShouldBe(["chat", "tool get_weather", "chat:1", "pause"]);
        (invocations, model.StreamCount).ShouldBe((1, 2));
    }

    [Fact]
    public async Task The_chat_options_of_an_agent_run_name_its_steps_and_stream_its_text_to_a_realtime_topic()
    {
        var model = new FakeChatClient((call, _) => call == 0 ? Calls(Call("call_1", "get_weather", "Prague")) : Says("It is sunny in Prague."));
        var agent = Pipeline(model).AsAIAgent(tools: [Weather(() => { })]);
        var topic = new RealtimeTopic("forecast:42", "tokens");
        await using var host = For(async context =>
        {
            var options = new ChatClientAgentRunOptions(new ChatOptions().WithInngestStepId("forecast").WithInngestRealtimeStream(topic));
            return (await agent.RunStreamingAsync("Weather?", options: options).ToAgentResponseAsync()).Text;
        });

        var run = await host.RunAsync("fn", Event(), Cancel);

        run.Steps.Select(step => step.Id).ShouldBe(["forecast", "forecast tool get_weather", "forecast:1"]);
        // Each model call of the agent streams to the topic; the one that asked for the tool had no text.
        run.Published.ShouldAllBe(published => published.Topic == topic && published.IsStream);
        string.Concat(run.Published.Select(published => published.Data.GetString())).ShouldBe("It is sunny in Prague.");
    }

    [Theory]
    [InlineData(true, 1, "Refunded 1042")]
    [InlineData(false, 0, "Tool call invocation was rejected by the approver and must not be retried. Too much")]
    public async Task An_agent_whose_tool_needs_approval_resumes_after_the_decision_that_the_run_waits_for(bool approved, int refunds, string told)
    {
        var invocations = 0;
        var refund = new ApprovalRequiredAIFunction(AIFunctionFactory.Create(
            (string order) =>
            {
                invocations++;
                return $"Refunded {order}";
            },
            "refund"));
        var model = new FakeChatClient((call, _) => call == 0
            ? Calls(new FunctionCallContent("call_1", "refund", new Dictionary<string, object?> { ["order"] = "1042" }))
            : Says("Done."));
        var agent = Pipeline(model).AsAIAgent(tools: [refund]);
        await using var host = For(async context =>
        {
            // The session is made again on every replay and filled again from the saved steps, so it needs no step itself.
            var session = await agent.CreateSessionAsync();
            var response = await agent.RunAsync("Refund order 1042", session);
            var request = response.Messages.SelectMany(message => message.Contents).OfType<ToolApprovalRequestContent>().Single();

            var decision = await context.Step.WaitForEventAsync("approval", new WaitForEventOptions("support/refund.decided", TimeSpan.FromDays(1)));
            var answer = request.CreateResponse(decision!.Data.GetProperty("approved").GetBoolean(), approved ? null : "Too much");
            return (await agent.RunAsync(new ChatMessage(ChatRole.User, [answer]), session)).Text;
        });

        var run = await host.RunAsync("fn", Event(), new StepMocks().WaitForEvent("approval", new InngestEvent("support/refund.decided", new { approved })), Cancel);

        run.Status.ShouldBe(TestRunStatus.Completed);
        run.GetOutput<string>().ShouldBe("Done.");
        invocations.ShouldBe(refunds);
        run.Steps.Select(step => step.Id).ShouldBe(approved ? ["chat", "approval", "tool refund", "chat:1"] : ["chat", "approval", "chat:1"]);
        var result = model.Messages[1].SelectMany(message => message.Contents).OfType<FunctionResultContent>().Single();
        (result.Result is JsonElement json ? json.GetString() : result.Result?.ToString()).ShouldBe(told);

        // The model was asked once before the wait, and once after it, on the replay that the decision started.
        model.CallCount.ShouldBe(2);
    }

    [Fact]
    public async Task A_session_saved_by_one_run_carries_the_conversation_into_the_next()
    {
        var model = new FakeChatClient((call, _) => Says(call == 0 ? "Prague is sunny." : "Tomorrow too."));
        var agent = Pipeline(model).AsAIAgent();
        await using var host = For(async context =>
        {
            // A conversation of several runs: each gets the session the last one returned, here in its event.
            var session = context.Event.Data.TryGetProperty("session", out var saved)
                ? await agent.DeserializeSessionAsync(saved)
                : await agent.CreateSessionAsync();
            var reply = await agent.RunAsync(context.Event.Data.GetProperty("message").GetString()!, session);
            return new { reply = reply.Text, session = await agent.SerializeSessionAsync(session) };
        });

        var first = await host.RunAsync("fn", Event(new { message = "Weather in Prague?" }), Cancel);
        var session = first.Output!.Value.GetProperty("session");
        var second = await host.RunAsync("fn", Event(new { message = "And tomorrow?", session }), Cancel);

        second.Output!.Value.GetProperty("reply").GetString().ShouldBe("Tomorrow too.");
        model.Messages[1].Select(message => (message.Role, message.Text)).ShouldBe(
        [
            (ChatRole.User, "Weather in Prague?"),
            (ChatRole.Assistant, "Prague is sunny."),
            (ChatRole.User, "And tomorrow?"),
        ]);
    }

    private static IChatClient Pipeline(IChatClient model) => new ChatClientBuilder(model).UseInngestFunctionInvocation().UseInngestSteps().Build();

    private static AIFunction Weather(Action invoked) =>
        AIFunctionFactory.Create(
            (string city) =>
            {
                invoked();
                return $"Sunny in {city}";
            },
            "get_weather",
            "Gets the weather of a city");

    private static FunctionCallContent Call(string id, string name, string city) => new(id, name, new Dictionary<string, object?> { ["city"] = city });

    private static ChatResponse Calls(params FunctionCallContent[] calls) =>
        new(new ChatMessage(ChatRole.Assistant, [.. calls])) { FinishReason = ChatFinishReason.ToolCalls };

    private static ChatResponse Says(string text) => new(new ChatMessage(ChatRole.Assistant, text));
}
