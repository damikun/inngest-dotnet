using System.Text.Json;
using System.Text.Json.Serialization;
using Inngest.Testing;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using static Inngest.Tests.Testing.TestHosts;

namespace Inngest.Tests.AI;

// A model call of a run is a step on the real engine: made once, and handed back from the run's memo on every replay.
public sealed class ModelCallStepTests
{
    [Fact]
    public async Task A_model_call_in_a_run_is_a_step_and_a_replay_does_not_make_it_again()
    {
        var model = new FakeChatClient();
        var chat = new ChatClientBuilder(model).UseInngestSteps().Build();
        await using var host = For(async context =>
        {
            var reply = await chat.GetResponseAsync("Say hello");
            await context.Step.SleepAsync("pause", TimeSpan.FromMinutes(5));
            return reply.Text;
        });

        var run = await host.RunAsync("fn", Event(), new StepMocks().Sleep("pause"), Cancel);

        run.Status.ShouldBe(TestRunStatus.Completed);
        run.GetOutput<string>().ShouldBe("Reply 0");
        model.CallCount.ShouldBe(1);
        run.Steps.Select(step => (step.Id, step.Type)).ShouldBe([("chat", StepType.AIWrap), ("pause", StepType.Sleep)]);
        run.GetStep("chat").Attempts.ShouldBe(1);
    }

    [Fact]
    public async Task Repeated_calls_get_numbered_ids_in_the_order_they_are_made()
    {
        var model = new FakeChatClient();
        var chat = new ChatClientBuilder(model).UseInngestSteps().Build();
        await using var host = For(async context =>
        {
            var first = await chat.GetResponseAsync("one");
            var second = await chat.GetResponseAsync("two");
            return $"{first.Text}|{second.Text}";
        });

        var run = await host.RunAsync("fn", Event(), Cancel);

        run.GetOutput<string>().ShouldBe("Reply 0|Reply 1");
        run.Steps.Select(step => step.Id).ShouldBe(["chat", "chat:1"]);
        model.CallCount.ShouldBe(2);
    }

    [Fact]
    public async Task A_call_can_name_its_step_and_the_model_never_sees_the_name()
    {
        var model = new FakeChatClient();
        var chat = new ChatClientBuilder(model).UseInngestSteps().Build();
        var options = new ChatOptions { Temperature = 0.5f }.WithInngestStepId("summarize");
        await using var host = For(async context =>
        {
            await chat.GetResponseAsync("one", options);
            await chat.GetResponseAsync("two", options);
            return await chat.GetResponseAsync("three");
        });

        var run = await host.RunAsync("fn", Event(), Cancel);

        run.Steps.Select(step => step.Id).ShouldBe(["summarize", "summarize:1", "chat"]);
        model.Options.Take(2).ShouldAllBe(received => received!.Temperature == 0.5f && (received.AdditionalProperties == null || received.AdditionalProperties.Count == 0));

        // The options are the caller's, who may use them again.
        options.AdditionalProperties.ShouldNotBeNull().Count.ShouldBe(1);
    }

    [Fact]
    public async Task Calls_started_together_are_parallel_steps_with_ids_in_the_order_of_the_calls()
    {
        var model = new FakeChatClient((_, messages) => new ChatResponse(new ChatMessage(ChatRole.Assistant, $"Answer to {messages[0].Text}")));
        var chat = new ChatClientBuilder(model).UseInngestSteps().Build();
        await using var host = For(async context =>
        {
            var replies = await Task.WhenAll(new[] { "a", "b", "c" }.Select(question => chat.GetResponseAsync(question)));
            return string.Join(", ", replies.Select(reply => reply.Text));
        });

        var run = await host.RunAsync("fn", Event(), Cancel);

        run.GetOutput<string>().ShouldBe("Answer to a, Answer to b, Answer to c");
        run.Steps.Select(step => step.Id).ShouldBe(["chat", "chat:1", "chat:2"]);
        model.CallCount.ShouldBe(3);
    }

    [Fact]
    public async Task A_call_is_made_with_the_messages_and_options_it_was_started_with_when_the_caller_changes_them_for_the_next_call()
    {
        var model = new FakeChatClient();
        var chat = new ChatClientBuilder(model).UseInngestSteps().Build();
        await using var host = For(async context =>
        {
            var messages = new List<ChatMessage> { new(ChatRole.User, "first") };
            var options = new ChatOptions { Temperature = 0.1f };
            var one = chat.GetResponseAsync(messages, options);

            messages.Add(new ChatMessage(ChatRole.User, "second"));
            options.Temperature = 0.9f;
            var two = chat.GetResponseAsync(messages, options);

            await Task.WhenAll(one, two);
            return null;
        });

        var run = await host.RunAsync("fn", Event(), Cancel);

        run.Status.ShouldBe(TestRunStatus.Completed);
        var calls = model.Messages.Zip(model.Options, (messages, options) => (messages.Count, options!.Temperature)).Order().ToArray();
        calls.ShouldBe([(1, 0.1f), (2, 0.9f)]);
        run.GetStep("chat").Options.GetProperty("input")[0].GetProperty("messages").GetArrayLength().ShouldBe(1);
        run.GetStep("chat:1").Options.GetProperty("input")[0].GetProperty("messages").GetArrayLength().ShouldBe(2);
    }

    [Fact]
    public async Task The_messages_and_options_are_the_input_the_Inngest_UI_shows()
    {
        var model = new FakeChatClient();
        var chat = new ChatClientBuilder(model).UseInngestSteps().Build();
        await using var host = For(async context =>
            (await chat.GetResponseAsync(
                [new ChatMessage(ChatRole.System, "Be brief."), new ChatMessage(ChatRole.User, "Say hello")],
                new ChatOptions { Temperature = 0.2f, MaxOutputTokens = 50, ModelId = "gpt-test" })).Text);

        var run = await host.RunAsync("fn", Event(), Cancel);

        var step = run.GetStep("chat");
        step.Options.GetProperty("type").GetString().ShouldBe("step.ai.wrap");
        var input = step.Options.GetProperty("input").EnumerateArray().ShouldHaveSingleItem();
        var messages = input.GetProperty("messages").EnumerateArray().ToArray();
        messages.Select(message => message.GetProperty("role").GetString()).ShouldBe(["system", "user"]);
        messages[1].GetProperty("contents")[0].GetProperty("text").GetString().ShouldBe("Say hello");
        var options = input.GetProperty("options");
        options.GetProperty("temperature").GetSingle().ShouldBe(0.2f);
        options.GetProperty("maxOutputTokens").GetInt32().ShouldBe(50);
        options.GetProperty("modelId").GetString().ShouldBe("gpt-test");
    }

    [Fact]
    public async Task The_input_can_be_left_out_and_the_step_is_then_a_plain_run_step()
    {
        var model = new FakeChatClient();
        var chat = new ChatClientBuilder(model).UseInngestSteps(options => options.RecordInput = false).Build();
        await using var host = For(async context => (await chat.GetResponseAsync("A secret prompt")).Text);

        var run = await host.RunAsync("fn", Event(), Cancel);

        run.GetOutput<string>().ShouldBe("Reply 0");
        var step = run.GetStep("chat");
        step.Type.ShouldBe(StepType.Run);
        step.Options.TryGetProperty("input", out _).ShouldBeFalse();
        model.Messages.ShouldHaveSingleItem()[0].Text.ShouldBe("A secret prompt");
    }

    [Fact]
    public async Task Input_that_is_not_JSON_is_left_out_with_one_warning_and_the_call_goes_through()
    {
        var model = new FakeChatClient();
        var chat = new ChatClientBuilder(model).UseInngestSteps().Build();

        // System.Type is a value System.Text.Json refuses to write.
        var options = new ChatOptions { AdditionalProperties = new() { ["marker"] = typeof(int) } };
        var services = Services(async context =>
        {
            var reply = await chat.GetResponseAsync("Hello", options);
            await context.Step.SleepAsync("pause", TimeSpan.FromMinutes(1));
            return reply.Text;
        });
        await using var host = new InngestTestHost(services.AddFakeLogging());

        var run = await host.RunAsync("fn", Event(), new StepMocks().Sleep("pause"), Cancel);

        run.GetOutput<string>().ShouldBe("Reply 0");
        run.GetStep("chat").Type.ShouldBe(StepType.Run);

        // The run replayed the step after the sleep, and the function's logger stays quiet about steps it has seen.
        var warning = host.Services.GetFakeLogCollector().GetSnapshot().Where(record => record.Level == LogLevel.Warning).ShouldHaveSingleItem();
        warning.Message.ShouldContain("The input of step chat could not be recorded");

        // The model got what the caller passed, including the property, which is the caller's to send.
        model.Options.ShouldHaveSingleItem()!.AdditionalProperties!["marker"].ShouldBe(typeof(int));
    }

    [Fact]
    public async Task A_model_call_that_fails_is_retried_as_a_step_and_made_again()
    {
        var model = new FakeChatClient { Fail = call => call == 0 ? new HttpRequestException("503 Service Unavailable") : null };
        var chat = new ChatClientBuilder(model).UseInngestSteps().Build();
        await using var host = For(async context => (await chat.GetResponseAsync("Hello")).Text);

        var run = await host.RunAsync("fn", Event(), Cancel);

        run.Status.ShouldBe(TestRunStatus.Completed);
        run.GetOutput<string>().ShouldBe("Reply 1");
        var step = run.GetStep("chat");
        step.Attempts.ShouldBe(2);
        step.Errors.ShouldHaveSingleItem().Message.ShouldBe("503 Service Unavailable");
        model.CallCount.ShouldBe(2);
    }

    [Fact]
    public async Task A_model_call_that_keeps_failing_throws_StepFailedException_where_it_was_awaited()
    {
        var model = new FakeChatClient { Fail = _ => new InvalidOperationException("The model is down") };
        var chat = new ChatClientBuilder(model).UseInngestSteps().Build();
        await using var host = For(async context =>
        {
            try
            {
                await chat.GetResponseAsync("Hello");
                return "answered";
            }
            catch (StepFailedException failure)
            {
                return failure.Message;
            }
        });

        var run = await host.RunAsync("fn", Event(), Cancel);

        run.GetOutput<string>().ShouldBe("The model is down");
        run.GetStep("chat").Status.ShouldBe(TestStepStatus.Failed);
    }

    [Fact]
    public async Task What_the_model_answered_survives_the_round_trip_through_the_memo()
    {
        var created = new DateTimeOffset(2026, 10, 30, 9, 0, 0, TimeSpan.Zero);
        var model = new FakeChatClient((_, _) => new ChatResponse(
        [
            new ChatMessage(ChatRole.Assistant,
            [
                new TextReasoningContent("The user wants the weather.") { ProtectedData = "signature" },
                new TextContent("Let me look that up."),
                new FunctionCallContent("call_1", "get_weather", new Dictionary<string, object?> { ["city"] = "Prague", ["days"] = 3, ["detail"] = new { hourly = true } }),
                new UriContent("https://example.com/map.png", "image/png"),
            ]) { MessageId = "msg_1", AuthorName = "assistant-1" },
        ])
        {
            ResponseId = "resp_1",
            ConversationId = "conv_1",
            ModelId = "gpt-test",
            CreatedAt = created,
            FinishReason = ChatFinishReason.ToolCalls,
            Usage = new UsageDetails { InputTokenCount = 120, OutputTokenCount = 30, TotalTokenCount = 150, CachedInputTokenCount = 100, ReasoningTokenCount = 12 },
            AdditionalProperties = new() { ["system_fingerprint"] = "fp_1" },
        });
        var chat = new ChatClientBuilder(model).UseInngestSteps().Build();
        ChatResponse? seen = null;
        await using var host = For(async context =>
        {
            seen = await chat.GetResponseAsync("What is the weather in Prague?");
            await context.Step.SleepAsync("pause", TimeSpan.FromMinutes(5));
            return null;
        });

        var run = await host.RunAsync("fn", Event(), new StepMocks().Sleep("pause"), Cancel);

        run.Status.ShouldBe(TestRunStatus.Completed);
        model.CallCount.ShouldBe(1);

        // The function ran again after the sleep, so this is what it was handed from the memo.
        seen.ShouldNotBeNull();
        seen.ResponseId.ShouldBe("resp_1");
        seen.ConversationId.ShouldBe("conv_1");
        seen.ModelId.ShouldBe("gpt-test");
        seen.CreatedAt.ShouldBe(created);
        seen.FinishReason.ShouldBe(ChatFinishReason.ToolCalls);
        seen.Usage.ShouldNotBeNull();
        (seen.Usage.InputTokenCount, seen.Usage.OutputTokenCount, seen.Usage.TotalTokenCount, seen.Usage.CachedInputTokenCount, seen.Usage.ReasoningTokenCount)
            .ShouldBe((120, 30, 150, 100, 12));
        ((JsonElement)seen.AdditionalProperties!["system_fingerprint"]!).GetString().ShouldBe("fp_1");
        var message = seen.Messages.ShouldHaveSingleItem();
        (message.Role, message.MessageId, message.AuthorName).ShouldBe((ChatRole.Assistant, "msg_1", "assistant-1"));
        message.Contents.Select(content => content.GetType()).ShouldBe([typeof(TextReasoningContent), typeof(TextContent), typeof(FunctionCallContent), typeof(UriContent)]);
        ((TextReasoningContent)message.Contents[0]).ProtectedData.ShouldBe("signature");
        var call = message.Contents.OfType<FunctionCallContent>().Single();
        (call.CallId, call.Name).ShouldBe(("call_1", "get_weather"));
        ((JsonElement)call.Arguments!["city"]!).GetString().ShouldBe("Prague");
        ((JsonElement)call.Arguments["days"]!).GetInt32().ShouldBe(3);
        ((JsonElement)call.Arguments["detail"]!).GetProperty("hourly").GetBoolean().ShouldBeTrue();
        message.Contents.OfType<UriContent>().Single().Uri.ShouldBe(new Uri("https://example.com/map.png"));
    }

    [Fact]
    public async Task A_step_mock_answers_a_model_call_with_the_JSON_of_a_chat_response()
    {
        var model = new FakeChatClient();
        var chat = new ChatClientBuilder(model).UseInngestSteps().Build();
        await using var host = For(async context => (await chat.GetResponseAsync("Say hello")).Text);
        var answer = new ChatResponse(new ChatMessage(ChatRole.Assistant, "Thank you for your order!"));

        var mocks = new StepMocks().Run("chat", JsonSerializer.SerializeToElement(answer, AIJsonUtilities.DefaultOptions));
        var run = await host.RunAsync("fn", Event(), mocks, Cancel);

        run.GetOutput<string>().ShouldBe("Thank you for your order!");
        run.GetStep("chat").Mocked.ShouldBeTrue();
        model.CallCount.ShouldBe(0);
    }

    [Fact]
    public async Task The_app_needs_no_serializer_registrations_for_the_types_of_the_chat_pipeline()
    {
        var model = new FakeChatClient((_, _) => new ChatResponse(new ChatMessage(ChatRole.Assistant,
            [new TextContent("Checking."), new FunctionCallContent("call_1", "lookup", new Dictionary<string, object?> { ["id"] = 7 })]))
        {
            Usage = new UsageDetails { InputTokenCount = 1, OutputTokenCount = 2 },
        });
        var chat = new ChatClientBuilder(model).UseInngestSteps().Build();
        var services = new ServiceCollection();
        services.AddInngest(options =>
        {
            options.AppId = AppId;

            // A resolver that knows nothing of Microsoft.Extensions.AI, as the context of an app on Native AOT.
            options.SerializerOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web) { TypeInfoResolver = AppOnlyJsonContext.Default };
        }).AddFunction(Options(), async (context, _) =>
        {
            var reply = await chat.GetResponseAsync("Look it up");
            await context.Step.SleepAsync("pause", TimeSpan.FromMinutes(1));
            return reply.Messages[0].Contents.OfType<FunctionCallContent>().Single().Name;
        });
        await using var host = new InngestTestHost(services);

        var run = await host.RunAsync("fn", Event(), new StepMocks().Sleep("pause"), Cancel);

        run.Status.ShouldBe(TestRunStatus.Completed);
        run.GetOutput<string>().ShouldBe("lookup");
    }
}

[JsonSerializable(typeof(string))]
internal sealed partial class AppOnlyJsonContext : JsonSerializerContext;
