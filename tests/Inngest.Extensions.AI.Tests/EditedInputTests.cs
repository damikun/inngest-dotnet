using Inngest.Tests.Execution;
using Microsoft.Extensions.AI;

namespace Inngest.Extensions.AI.Tests;

// The wire between the chat steps and Inngest, on the engine alone: what Inngest memoizes for a step, and the input it sends
// back after an edit in its UI.
public sealed class EditedInputTests : IDisposable
{
    private readonly EngineHost _host = new();

    public void Dispose() => _host.Dispose();

    [Fact]
    public async Task A_memoized_response_is_plain_Microsoft_Extensions_AI_JSON_and_the_model_is_not_called()
    {
        var model = new FakeChatClient();
        var chat = new ChatClientBuilder(model).UseInngestSteps().Build();
        var function = TestFunction.Define(async context => (await chat.GetResponseAsync("Say hello")).Text);
        const string Memo = """
            {"messages":[{"role":"assistant","contents":[{"$type":"text","text":"Remembered"}]}],"responseId":"resp_1","usage":{"inputTokenCount":3,"outputTokenCount":1}}
            """;

        var result = await _host.CallAsync(function, Call.New().Data("chat", Memo));

        result.StatusCode.ShouldBe(200);
        result.Body.ShouldBe("\"Remembered\"");
        model.CallCount.ShouldBe(0);
    }

    [Fact]
    public async Task The_model_gets_what_the_caller_passed_and_not_a_copy_read_back_from_JSON_unless_the_input_was_edited()
    {
        var model = new FakeChatClient();
        var chat = new ChatClientBuilder(model).UseInngestSteps().Build();
        var message = new ChatMessage(ChatRole.User, "Write a haiku") { RawRepresentation = "the provider's own message" };
        var options = new ChatOptions { Temperature = 0.9f, RawRepresentationFactory = _ => "the provider's own request" };
        var function = TestFunction.Define(async context => (await chat.GetResponseAsync([message], options)).Text);

        var result = await _host.CallAsync(function, Call.New());

        // None of the raw representations are JSON, so a copy read back from the recorded input would have lost them.
        result.OpCodes.ShouldBe(["StepRun"]);
        model.Messages.ShouldHaveSingleItem().ShouldHaveSingleItem().ShouldBeSameAs(message);
        var received = model.Options.ShouldHaveSingleItem()!;
        (received.Temperature, received.RawRepresentationFactory!(model)).ShouldBe((0.9f, "the provider's own request"));
    }

    [Fact]
    public async Task An_input_edited_in_the_Inngest_UI_reaches_the_model_with_the_tools_of_the_call()
    {
        var model = new FakeChatClient();
        var chat = new ChatClientBuilder(model).UseInngestSteps().Build();
        var tools = new List<AITool> { AIFunctionFactory.Create(() => "unused", "noop") };
        var function = TestFunction.Define(async context =>
            (await chat.GetResponseAsync("Write a haiku", new ChatOptions { Temperature = 0.9f, Tools = tools })).Text);
        const string Edited = """
            [{"messages":[{"role":"system","contents":[{"$type":"text","text":"Be playful."}]},{"role":"user","contents":[{"$type":"text","text":"Write a limerick"}]}],"options":{"temperature":0.1,"modelId":"other-model"}}]
            """;

        var result = await _host.CallAsync(function, Call.New().Input("chat", Edited));

        result.OpCodes.ShouldBe(["StepRun"]);
        result.Op().GetProperty("opts").GetProperty("input").GetRawText().ShouldBe(Edited.Trim());
        model.Messages.ShouldHaveSingleItem().Select(message => (message.Role, message.Text)).ShouldBe([(ChatRole.System, "Be playful."), (ChatRole.User, "Write a limerick")]);
        var options = model.Options.ShouldHaveSingleItem()!;
        (options.Temperature, options.ModelId).ShouldBe((0.1f, "other-model"));
        options.Tools.ShouldBe(tools);
    }

    [Fact]
    public async Task An_edit_that_leaves_the_options_out_keeps_the_tools_of_the_call()
    {
        var model = new FakeChatClient();
        var chat = new ChatClientBuilder(model).UseInngestSteps().Build();
        var tools = new List<AITool> { AIFunctionFactory.Create(() => "unused", "noop") };
        var function = TestFunction.Define(async context => (await chat.GetResponseAsync("Write a haiku", new ChatOptions { Tools = tools })).Text);
        const string Edited = """[{"messages":[{"role":"user","contents":[{"$type":"text","text":"Write a limerick"}]}]}]""";

        await _host.CallAsync(function, Call.New().Input("chat", Edited));

        model.Options.ShouldHaveSingleItem()!.Tools.ShouldBe(tools);
    }

    [Fact]
    public async Task An_edited_input_without_messages_fails_the_step_for_good()
    {
        var model = new FakeChatClient();
        var chat = new ChatClientBuilder(model).UseInngestSteps().Build();
        var function = TestFunction.Define(async context => (await chat.GetResponseAsync("Write a haiku")).Text);

        var result = await _host.CallAsync(function, Call.New().Input("chat", """[{"prompt":"Write a limerick"}]"""));

        result.OpCodes.ShouldBe(["StepFailed"]);
        result.Op().GetProperty("error").GetProperty("message").GetString()!.ShouldContain("\"messages\" array");
        model.CallCount.ShouldBe(0);
    }

    [Fact]
    public async Task Arguments_edited_in_the_Inngest_UI_reach_the_tool()
    {
        var model = new FakeChatClient();
        var chat = new ChatClientBuilder(model).UseInngestFunctionInvocation().UseInngestSteps().Build();
        var asked = new List<string>();
        var weather = AIFunctionFactory.Create(
            (string city) =>
            {
                asked.Add(city);
                return $"Sunny in {city}";
            },
            "get_weather",
            "Gets the weather of a city");
        var function = TestFunction.Define(async context => (await chat.GetResponseAsync("Weather?", new ChatOptions { Tools = [weather] })).Text);
        const string Calling = """
            {"messages":[{"role":"assistant","contents":[{"$type":"functionCall","callId":"call_1","name":"get_weather","arguments":{"city":"Prague"}}]}],"finishReason":"tool_calls"}
            """;
        const string Edited = """[{"name":"get_weather","callId":"call_1","arguments":{"city":"Brno"}}]""";

        var result = await _host.CallAsync(function, Call.New().Data("chat", Calling).Input("tool get_weather", Edited));

        result.OpCodes.ShouldBe(["StepRun"]);
        result.Op().GetProperty("opts").GetProperty("input").GetRawText().ShouldBe(Edited);
        result.Op().GetProperty("data").GetProperty("result").GetString().ShouldBe("Sunny in Brno");
        asked.ShouldBe(["Brno"]);
        model.CallCount.ShouldBe(0);
    }

    [Fact]
    public async Task An_edited_input_without_arguments_fails_the_tool_step_for_good()
    {
        var model = new FakeChatClient();
        var chat = new ChatClientBuilder(model).UseInngestFunctionInvocation().UseInngestSteps().Build();
        var weather = AIFunctionFactory.Create((string city) => $"Sunny in {city}", "get_weather", "Gets the weather of a city");
        var function = TestFunction.Define(async context => (await chat.GetResponseAsync("Weather?", new ChatOptions { Tools = [weather] })).Text);
        const string Calling = """
            {"messages":[{"role":"assistant","contents":[{"$type":"functionCall","callId":"call_1","name":"get_weather","arguments":{"city":"Prague"}}]}]}
            """;

        var result = await _host.CallAsync(function, Call.New().Data("chat", Calling).Input("tool get_weather", """[{"city":"Brno"}]"""));

        result.OpCodes.ShouldBe(["StepFailed"]);
        result.Op().GetProperty("error").GetProperty("message").GetString()!.ShouldContain("\"arguments\" object");
    }
}
