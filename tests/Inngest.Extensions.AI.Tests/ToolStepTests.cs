using System.Text.Json;
using Inngest.Testing;
using Microsoft.Extensions.AI;
using static Inngest.Tests.Testing.TestHosts;

namespace Inngest.Extensions.AI.Tests;

// The tools that FunctionInvokingChatClient invokes for the model are steps of the run: a tool runs once, and a replay
// gets its result from the memo, with the model calls around it also steps.
public sealed class ToolStepTests
{
    [Fact]
    public async Task A_tool_call_is_a_step_that_runs_once_between_the_model_calls_that_are_steps_too()
    {
        var invocations = 0;
        var weather = Weather(() => invocations++);
        var model = new FakeChatClient((call, _) => call == 0 ? Calls(Call("call_1", "get_weather", "Prague")) : Says("It is sunny in Prague."));
        var chat = Pipeline(model);
        await using var host = For(async context =>
        {
            var reply = await chat.GetResponseAsync("What is the weather in Prague?", new ChatOptions { Tools = [weather] });
            await context.Step.SleepAsync("pause", TimeSpan.FromMinutes(5));
            return reply.Text;
        });

        var run = await host.RunAsync("fn", Event(), new StepMocks().Sleep("pause"), Cancel);

        run.Status.ShouldBe(TestRunStatus.Completed);
        run.GetOutput<string>()!.ShouldContain("It is sunny in Prague.");
        invocations.ShouldBe(1);
        model.CallCount.ShouldBe(2);
        run.Steps.Select(step => (step.Id, step.Type)).ShouldBe(
        [
            ("chat", StepType.AIWrap),
            ("tool get_weather", StepType.AIWrap),
            ("chat:1", StepType.AIWrap),
            ("pause", StepType.Sleep),
        ]);
        run.Steps.ShouldAllBe(step => step.Attempts <= 1);

        // The model got the result of the tool, as the JSON a function made with AIFunctionFactory returns.
        var result = model.Messages[1].SelectMany(message => message.Contents).OfType<FunctionResultContent>().Single();
        result.CallId.ShouldBe("call_1");
        ((JsonElement)result.Result!).GetString().ShouldBe("Sunny in Prague");
    }

    [Fact]
    public async Task The_arguments_are_the_input_of_the_tool_step_and_the_result_is_its_output()
    {
        var model = new FakeChatClient((call, _) => call == 0 ? Calls(Call("call_1", "get_weather", "Prague")) : Says("Done."));
        var chat = Pipeline(model);
        await using var host = For(async context => (await chat.GetResponseAsync("Weather?", new ChatOptions { Tools = [Weather(() => { })] })).Text);

        var run = await host.RunAsync("fn", Event(), Cancel);

        var step = run.GetStep("tool get_weather");
        step.Options.GetProperty("type").GetString().ShouldBe("step.ai.wrap");
        var input = step.Options.GetProperty("input").EnumerateArray().ShouldHaveSingleItem();
        input.GetProperty("name").GetString().ShouldBe("get_weather");
        input.GetProperty("callId").GetString().ShouldBe("call_1");
        input.GetProperty("arguments").GetProperty("city").GetString().ShouldBe("Prague");
        step.Output!.Value.GetProperty("result").GetString().ShouldBe("Sunny in Prague");
        step.Output.Value.TryGetProperty("terminate", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task The_input_of_tool_steps_can_be_left_out_too()
    {
        var model = new FakeChatClient((call, _) => call == 0 ? Calls(Call("call_1", "get_weather", "Prague")) : Says("Done."));
        var chat = new ChatClientBuilder(model)
            .UseInngestFunctionInvocation(configureSteps: options => options.RecordInput = false)
            .UseInngestSteps(options => options.RecordInput = false)
            .Build();
        await using var host = For(async context => (await chat.GetResponseAsync("Weather?", new ChatOptions { Tools = [Weather(() => { })] })).Text);

        var run = await host.RunAsync("fn", Event(), Cancel);

        run.Steps.Select(step => (step.Id, step.Type)).ShouldBe([("chat", StepType.Run), ("tool get_weather", StepType.Run), ("chat:1", StepType.Run)]);
        run.Steps.Any(step => step.Options.TryGetProperty("input", out _)).ShouldBeFalse();
    }

    [Fact]
    public async Task Tools_the_model_calls_together_are_parallel_steps_when_the_client_allows_it()
    {
        var invocations = 0;
        var model = new FakeChatClient((call, _) => call == 0
            ? Calls(Call("call_1", "get_weather", "Prague"), Call("call_2", "get_weather", "Brno"))
            : Says("Both are sunny."));
        var chat = new ChatClientBuilder(model)
            .UseInngestFunctionInvocation(client => client.AllowConcurrentInvocation = true)
            .UseInngestSteps()
            .Build();
        await using var host = For(async context => (await chat.GetResponseAsync("Weather?", new ChatOptions { Tools = [Weather(() => Interlocked.Increment(ref invocations))] })).Text);

        var run = await host.RunAsync("fn", Event(), Cancel);

        run.Steps.Select(step => step.Id).ShouldBe(["chat", "tool get_weather", "tool get_weather:1", "chat:1"]);
        invocations.ShouldBe(2);

        // The ids follow the order of the model's calls, so each result went back to its own call.
        model.Messages[1].SelectMany(message => message.Contents).OfType<FunctionResultContent>()
            .Select(result => (result.CallId, ((JsonElement)result.Result!).GetString()))
            .ShouldBe([("call_1", "Sunny in Prague"), ("call_2", "Sunny in Brno")]);
    }

    [Fact]
    public async Task A_tool_that_throws_is_retried_and_reported_to_the_model_once_the_retries_are_used_up()
    {
        var attempts = 0;
        var model = new FakeChatClient((call, _) => call == 0 ? Calls(Call("call_1", "get_weather", "Prague")) : Says("I could not get the weather."));
        var chat = Pipeline(model);
        var broken = AIFunctionFactory.Create(
            (string city) =>
            {
                attempts++;
                throw new InvalidOperationException("The weather service is down");
            },
            "get_weather",
            "Gets the weather of a city");
        await using var host = For(async context => (await chat.GetResponseAsync("Weather?", new ChatOptions { Tools = [broken] })).Text, retries: 2);

        var run = await host.RunAsync("fn", Event(), Cancel);

        run.Status.ShouldBe(TestRunStatus.Completed);
        attempts.ShouldBe(3);
        var step = run.GetStep("tool get_weather");
        (step.Status, step.Attempts).ShouldBe((TestStepStatus.Failed, 3));

        // FunctionInvokingChatClient tells the model that the tool failed, as it does for any tool that throws.
        var result = model.Messages[1].SelectMany(message => message.Contents).OfType<FunctionResultContent>().Single();
        result.Result.ShouldBe("Error: Function failed.");
        result.Exception.ShouldBeOfType<StepFailedException>().Message.ShouldBe("The weather service is down");
    }

    [Fact]
    public async Task A_tool_that_throws_ArgumentException_fails_at_once_because_the_same_arguments_fail_again()
    {
        var attempts = 0;
        var model = new FakeChatClient((call, _) => call == 0 ? Calls(Call("call_1", "get_weather", "Atlantis")) : Says("There is no such city."));
        var chat = Pipeline(model);
        var strict = AIFunctionFactory.Create(
            (string city) =>
            {
                attempts++;
                throw new ArgumentException($"Unknown city {city}.");
            },
            "get_weather",
            "Gets the weather of a city");
        await using var host = For(async context => (await chat.GetResponseAsync("Weather?", new ChatOptions { Tools = [strict] })).Text, retries: 3);

        var run = await host.RunAsync("fn", Event(), Cancel);

        run.Status.ShouldBe(TestRunStatus.Completed);
        attempts.ShouldBe(1);
        var step = run.GetStep("tool get_weather");
        (step.Status, step.Attempts).ShouldBe((TestStepStatus.Failed, 1));
        model.CallCount.ShouldBe(2);
    }

    [Fact]
    public async Task The_tool_asking_to_stop_the_model_calls_stops_them_on_every_replay()
    {
        var invocations = 0;
        var model = new FakeChatClient((_, _) => Calls(Call("call_1", "get_weather", "Prague")));
        var chat = new ChatClientBuilder(model)
            .UseInngestFunctionInvocation(client => client.FunctionInvoker = async (context, cancellationToken) =>
            {
                var result = await context.Function.InvokeAsync(context.Arguments, cancellationToken);
                context.Terminate = true;
                return result;
            })
            .UseInngestSteps()
            .Build();
        ChatResponse? seen = null;
        await using var host = For(async context =>
        {
            seen = await chat.GetResponseAsync("Weather?", new ChatOptions { Tools = [Weather(() => invocations++)] });
            await context.Step.SleepAsync("pause", TimeSpan.FromMinutes(1));
            return null;
        });

        var run = await host.RunAsync("fn", Event(), new StepMocks().Sleep("pause"), Cancel);

        run.Status.ShouldBe(TestRunStatus.Completed);

        // The model was asked once, in the call that ran the tool and in the replay that followed, which had to stop at the same place.
        model.CallCount.ShouldBe(1);
        invocations.ShouldBe(1);
        run.Steps.Select(step => step.Id).ShouldBe(["chat", "tool get_weather", "pause"]);
        run.GetStep("tool get_weather").Output!.Value.GetProperty("terminate").GetBoolean().ShouldBeTrue();
        seen!.Messages.Select(message => message.Role).ShouldBe([ChatRole.Assistant, ChatRole.Tool]);
    }

    [Fact]
    public async Task A_tool_can_reach_the_context_of_its_call_and_stop_the_model_calls_with_it()
    {
        string? called = null;
        var model = new FakeChatClient((_, _) => Calls(Call("call_1", "finish", "Prague")));
        var chat = Pipeline(model);
        var finish = AIFunctionFactory.Create(
            (string city) =>
            {
                var context = FunctionInvokingChatClient.CurrentContext!;
                called = context.Function.Name;
                context.Terminate = true;
                return $"Done with {city}";
            },
            "finish",
            "Ends the conversation");
        await using var host = For(async context =>
        {
            await chat.GetResponseAsync("Finish up", new ChatOptions { Tools = [finish] });
            await context.Step.SleepAsync("pause", TimeSpan.FromMinutes(1));
            return null;
        });

        var run = await host.RunAsync("fn", Event(), new StepMocks().Sleep("pause"), Cancel);

        run.Status.ShouldBe(TestRunStatus.Completed);
        called.ShouldBe("finish");
        model.CallCount.ShouldBe(1);
        run.Steps.Select(step => step.Id).ShouldBe(["chat", "tool finish", "pause"]);
    }

    [Fact]
    public async Task A_function_invoker_of_the_app_runs_inside_the_step()
    {
        var invoked = new List<string>();
        var model = new FakeChatClient((call, _) => call == 0 ? Calls(Call("call_1", "get_weather", "Prague")) : Says("Done."));
        var chat = new ChatClientBuilder(model)
            .UseInngestFunctionInvocation(client => client.FunctionInvoker = (context, cancellationToken) =>
            {
                invoked.Add(context.Function.Name);
                return new ValueTask<object?>("Overcast, by the app's invoker");
            })
            .UseInngestSteps()
            .Build();
        await using var host = For(async context =>
        {
            await chat.GetResponseAsync("Weather?", new ChatOptions { Tools = [Weather(() => { })] });
            await context.Step.SleepAsync("pause", TimeSpan.FromMinutes(1));
            return null;
        });

        var run = await host.RunAsync("fn", Event(), new StepMocks().Sleep("pause"), Cancel);

        invoked.ShouldBe(["get_weather"]);
        run.GetStep("tool get_weather").Output!.Value.GetProperty("result").GetString().ShouldBe("Overcast, by the app's invoker");
    }

    [Fact]
    public async Task A_result_that_is_not_JSON_yet_is_saved_with_the_options_of_its_function_and_replays_as_JSON()
    {
        var model = new FakeChatClient((call, _) => call == 0 ? Calls(Call("call_1", "forecast", "Prague")) : Says("Done."));
        var chat = Pipeline(model);

        // A result the function does not marshal to JSON itself, as a function of its own may return.
        var forecast = AIFunctionFactory.Create(
            (string city) => new Forecast(city, 21),
            new AIFunctionFactoryOptions { Name = "forecast", MarshalResult = (result, _, _) => new ValueTask<object?>(result) });
        await using var host = For(async context => (await chat.GetResponseAsync("Forecast?", new ChatOptions { Tools = [forecast] })).Text);

        var run = await host.RunAsync("fn", Event(), Cancel);

        run.Status.ShouldBe(TestRunStatus.Completed);
        var stored = run.GetStep("tool forecast").Output!.Value.GetProperty("result");
        (stored.GetProperty("city").GetString(), stored.GetProperty("degrees").GetInt32()).ShouldBe(("Prague", 21));
        var given = model.Messages[1].SelectMany(message => message.Contents).OfType<FunctionResultContent>().Single();
        ((JsonElement)given.Result!).GetProperty("degrees").GetInt32().ShouldBe(21);
    }

    [Fact]
    public async Task A_tool_without_a_result_replays_without_one()
    {
        var model = new FakeChatClient((call, _) => call == 0 ? Calls(Call("call_1", "get_weather", "Prague")) : Says("Done."));
        var chat = Pipeline(model);
        var quiet = AIFunctionFactory.Create((string city) => { }, "get_weather", "Does something");
        await using var host = For(async context => (await chat.GetResponseAsync("Weather?", new ChatOptions { Tools = [quiet] })).Text);

        var run = await host.RunAsync("fn", Event(), Cancel);

        run.GetStep("tool get_weather").Output!.Value.GetProperty("result").ValueKind.ShouldBe(JsonValueKind.Null);

        // FunctionInvokingChatClient says what it says for a function that returned nothing.
        var given = model.Messages[1].SelectMany(message => message.Contents).OfType<FunctionResultContent>().Single();
        given.Result.ShouldBe("Success: Function completed.");
    }

    [Fact]
    public async Task Outside_a_run_the_tools_are_invoked_directly()
    {
        var invocations = 0;
        var model = new FakeChatClient((call, _) => call == 0 ? Calls(Call("call_1", "get_weather", "Prague")) : Says("It is sunny."));
        var chat = Pipeline(model);

        var reply = await chat.GetResponseAsync("Weather?", new ChatOptions { Tools = [Weather(() => invocations++)] }, TestContext.Current.CancellationToken);

        reply.Text.ShouldContain("It is sunny.");
        invocations.ShouldBe(1);
        model.CallCount.ShouldBe(2);
    }

    [Fact]
    public async Task Inside_a_step_handler_the_model_and_its_tools_are_called_directly()
    {
        var invocations = 0;
        var model = new FakeChatClient((call, _) => call == 0 ? Calls(Call("call_1", "get_weather", "Prague")) : Says("It is sunny."));
        var chat = Pipeline(model);
        await using var host = For(async context =>
            await context.Step.RunAsync("agent", async () => (await chat.GetResponseAsync("Weather?", new ChatOptions { Tools = [Weather(() => invocations++)] })).Text));

        var run = await host.RunAsync("fn", Event(), Cancel);

        run.GetOutput<string>()!.ShouldContain("It is sunny.");
        run.Steps.Select(step => step.Id).ShouldBe(["agent"]);
        (invocations, model.CallCount).ShouldBe((1, 2));
    }

    [Fact]
    public async Task A_stream_with_tool_calls_makes_the_same_steps()
    {
        var invocations = 0;
        var model = new FakeChatClient((call, _) => call == 0 ? Calls(Call("call_1", "get_weather", "Prague")) : Says("It is sunny in Prague."));
        var chat = Pipeline(model);
        await using var host = For(async context =>
        {
            var reply = await chat.GetStreamingResponseAsync("Weather?", new ChatOptions { Tools = [Weather(() => invocations++)] }).ToChatResponseAsync();
            await context.Step.SleepAsync("pause", TimeSpan.FromMinutes(1));
            return reply.Text;
        });

        var run = await host.RunAsync("fn", Event(), new StepMocks().Sleep("pause"), Cancel);

        run.GetOutput<string>()!.ShouldContain("It is sunny in Prague.");
        run.Steps.Select(step => step.Id).ShouldBe(["chat", "tool get_weather", "chat:1", "pause"]);
        (invocations, model.StreamCount).ShouldBe((1, 2));
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

    private sealed record Forecast(string City, int Degrees);
}
