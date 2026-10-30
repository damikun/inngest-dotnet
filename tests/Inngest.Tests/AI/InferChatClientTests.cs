using System.Text.Json;
using Inngest.Testing;
using Microsoft.Extensions.AI;
using static Inngest.Tests.Testing.TestHosts;

namespace Inngest.Tests.AI;

// A chat client whose calls are inference steps: Inngest sends the request in the OpenAI chat format, and the response of
// the provider is what the step saves.
public sealed class InferChatClientTests
{
    private static readonly Uri Endpoint = new("https://api.openai.com/v1/chat/completions");

    private const string Hello = """
        {"id":"chatcmpl-1","object":"chat.completion","created":1761800000,"model":"gpt-4o-mini","choices":[{"index":0,"message":{"role":"assistant","content":"Hello!"},"finish_reason":"stop"}],"usage":{"prompt_tokens":9,"completion_tokens":2,"total_tokens":11}}
        """;

    private const string WantsWeather = """
        {"id":"chatcmpl-2","model":"gpt-4o-mini","choices":[{"index":0,"message":{"role":"assistant","content":null,"tool_calls":[{"id":"call_1","type":"function","function":{"name":"get_weather","arguments":"{\"city\":\"Prague\"}"}}]},"finish_reason":"tool_calls"}]}
        """;

    private const string Sunny = """
        {"id":"chatcmpl-3","model":"gpt-4o-mini","choices":[{"index":0,"message":{"role":"assistant","content":"It is sunny in Prague."},"finish_reason":"stop"}]}
        """;

    [Fact]
    public async Task A_call_is_an_inference_step_with_the_request_in_the_OpenAI_chat_format()
    {
        var client = new InngestInferChatClient(Endpoint, "gpt-4o-mini", "sk-test", new Dictionary<string, string> { ["OpenAI-Organization"] = "org-1" });
        ChatResponse? seen = null;
        await using var host = For(async context =>
        {
            seen = await client.GetResponseAsync(
                [new ChatMessage(ChatRole.System, "Be brief."), new ChatMessage(ChatRole.User, "Say hello")],
                new ChatOptions { Temperature = 0.2f, MaxOutputTokens = 50, StopSequences = ["END"], Seed = 7 });
            return seen.Text;
        });

        var run = await host.RunAsync("fn", Event(), new StepMocks().Infer("chat", Parse(Hello)), Cancel);

        run.GetOutput<string>().ShouldBe("Hello!");
        var step = run.GetStep("chat");
        step.Type.ShouldBe(StepType.AIInfer);
        step.Options.GetProperty("format").GetString().ShouldBe("openai-chat");
        step.Options.GetProperty("url").GetString().ShouldBe(Endpoint.AbsoluteUri);
        step.Options.GetProperty("auth_key").GetString().ShouldBe("sk-test");
        step.Options.GetProperty("headers").GetProperty("OpenAI-Organization").GetString().ShouldBe("org-1");
        step.Options.GetProperty("body").GetRawText().ShouldBe(
            """{"model":"gpt-4o-mini","messages":[{"role":"system","content":"Be brief."},{"role":"user","content":"Say hello"}],"temperature":0.2,"max_completion_tokens":50,"seed":7,"stop":["END"]}""");

        seen!.ResponseId.ShouldBe("chatcmpl-1");
        seen.ModelId.ShouldBe("gpt-4o-mini");
        seen.FinishReason.ShouldBe(ChatFinishReason.Stop);
        seen.CreatedAt.ShouldBe(DateTimeOffset.FromUnixTimeSeconds(1761800000));
        (seen.Usage!.InputTokenCount, seen.Usage.OutputTokenCount, seen.Usage.TotalTokenCount).ShouldBe((9, 2, 11));
    }

    [Fact]
    public async Task Tools_and_tool_results_travel_both_ways_through_a_function_invoking_pipeline()
    {
        var weather = AIFunctionFactory.Create((string city) => $"Sunny in {city}", "get_weather", "Gets the weather of a city");
        var chat = new ChatClientBuilder(new InngestInferChatClient(Endpoint, "gpt-4o-mini")).UseInngestFunctionInvocation().Build();
        await using var host = For(async context =>
            (await chat.GetResponseAsync("Weather in Prague?", new ChatOptions { Tools = [weather], ToolMode = ChatToolMode.Auto, AllowMultipleToolCalls = false })).Text);

        var mocks = new StepMocks().Infer("chat", Parse(WantsWeather)).Infer("chat:1", Parse(Sunny));
        var run = await host.RunAsync("fn", Event(), mocks, Cancel);

        run.Status.ShouldBe(TestRunStatus.Completed);
        run.GetOutput<string>()!.ShouldContain("It is sunny in Prague.");
        run.Steps.Select(step => (step.Id, step.Type)).ShouldBe([("chat", StepType.AIInfer), ("tool get_weather", StepType.AIWrap), ("chat:1", StepType.AIInfer)]);

        var first = run.GetStep("chat").Options.GetProperty("body");
        var tool = first.GetProperty("tools").EnumerateArray().ShouldHaveSingleItem();
        tool.GetProperty("type").GetString().ShouldBe("function");
        tool.GetProperty("function").GetProperty("name").GetString().ShouldBe("get_weather");
        tool.GetProperty("function").GetProperty("description").GetString().ShouldBe("Gets the weather of a city");
        tool.GetProperty("function").GetProperty("parameters").GetProperty("properties").GetProperty("city").GetProperty("type").GetString().ShouldBe("string");
        first.GetProperty("tool_choice").GetString().ShouldBe("auto");
        first.GetProperty("parallel_tool_calls").GetBoolean().ShouldBeFalse();

        var second = run.GetStep("chat:1").Options.GetProperty("body").GetProperty("messages").EnumerateArray().ToArray();
        second.Select(message => message.GetProperty("role").GetString()).ShouldBe(["user", "assistant", "tool"]);
        var call = second[1].GetProperty("tool_calls").EnumerateArray().ShouldHaveSingleItem();
        second[1].GetProperty("content").ValueKind.ShouldBe(JsonValueKind.Null);
        (call.GetProperty("id").GetString(), call.GetProperty("function").GetProperty("name").GetString()).ShouldBe(("call_1", "get_weather"));
        call.GetProperty("function").GetProperty("arguments").GetString().ShouldBe("""{"city":"Prague"}""");
        second[2].GetProperty("tool_call_id").GetString().ShouldBe("call_1");
        second[2].GetProperty("content").GetString().ShouldBe("Sunny in Prague");
    }

    [Fact]
    public async Task Images_and_a_JSON_schema_go_out_in_the_forms_the_API_takes()
    {
        var client = new InngestInferChatClient(Endpoint, "gpt-4o-mini");
        using var schema = JsonDocument.Parse("""{"type":"object","properties":{"answer":{"type":"string"}}}""");
        await using var host = For(async context => (await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, [new TextContent("What is this?"), new UriContent("https://example.com/cat.png", "image/png")])],
            new ChatOptions { ResponseFormat = ChatResponseFormat.ForJsonSchema(schema.RootElement.Clone(), "answer") })).Text);

        var run = await host.RunAsync("fn", Event(), new StepMocks().Infer("chat", Parse(Hello)), Cancel);

        var body = run.GetStep("chat").Options.GetProperty("body");
        var parts = body.GetProperty("messages")[0].GetProperty("content").EnumerateArray().ToArray();
        parts[0].GetProperty("text").GetString().ShouldBe("What is this?");
        parts[1].GetProperty("image_url").GetProperty("url").GetString().ShouldBe("https://example.com/cat.png");
        var format = body.GetProperty("response_format");
        (format.GetProperty("type").GetString(), format.GetProperty("json_schema").GetProperty("name").GetString()).ShouldBe(("json_schema", "answer"));
        format.GetProperty("json_schema").GetProperty("schema").GetProperty("properties").GetProperty("answer").GetProperty("type").GetString().ShouldBe("string");
    }

    [Fact]
    public async Task A_stream_hands_out_the_saved_response_as_updates_and_a_call_can_name_its_step()
    {
        var client = new InngestInferChatClient(Endpoint, "gpt-4o-mini");
        await using var host = For(async context =>
            (await client.GetStreamingResponseAsync("Say hello", new ChatOptions().WithInngestStepId("greet")).ToChatResponseAsync()).Text);

        var run = await host.RunAsync("fn", Event(), new StepMocks().Infer("greet", Parse(Hello)), Cancel);

        run.GetOutput<string>().ShouldBe("Hello!");
        run.Steps.Select(step => step.Id).ShouldBe(["greet"]);
        run.GetStep("greet").Options.GetProperty("body").GetProperty("model").GetString().ShouldBe("gpt-4o-mini");
    }

    [Fact]
    public async Task Outside_a_run_and_inside_a_step_handler_there_is_no_step_to_make_and_the_call_fails_clearly()
    {
        var client = new InngestInferChatClient(Endpoint, "gpt-4o-mini");

        var outside = Should.Throw<InvalidOperationException>(() => client.GetResponseAsync("Hello", cancellationToken: TestContext.Current.CancellationToken));
        outside.Message.ShouldContain("only be made by the code of a run");

        await using var host = For(async context =>
            await context.Step.RunAsync("inside", async () =>
            {
                try
                {
                    await client.GetResponseAsync("Hello");
                    return "answered";
                }
                catch (InvalidOperationException exception)
                {
                    return exception.Message;
                }
            }));
        var run = await host.RunAsync("fn", Event(), Cancel);
        run.GetOutput<string>()!.ShouldContain("only be made by the code of a run");
    }

    [Fact]
    public void The_client_describes_itself()
    {
        var client = new InngestInferChatClient(Endpoint, "gpt-4o-mini");

        var metadata = client.GetService<ChatClientMetadata>().ShouldNotBeNull();

        (metadata.ProviderName, metadata.ProviderUri, metadata.DefaultModelId).ShouldBe(("inngest-infer", Endpoint, "gpt-4o-mini"));
        client.GetService<IChatClient>().ShouldBeSameAs(client);
    }

    private static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
