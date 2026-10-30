using System.Text.Json;
using Inngest.Tests.Execution;
using Microsoft.Extensions.AI;
using static Inngest.Tests.Checkpointing.Checkpointed;

namespace Inngest.Extensions.AI.Tests;

// With checkpointing, the steps of a chat run one after the other in one call, and each is saved before the next runs.
public sealed class CheckpointedChatTests : IDisposable
{
    private readonly EngineHost _host = new();

    public void Dispose() => _host.Dispose();

    [Fact]
    public async Task A_model_call_a_tool_and_a_second_model_call_run_in_one_call_and_are_each_saved()
    {
        var tools = 0;
        var model = new FakeChatClient((call, _) => call == 0
            ? new ChatResponse(new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("call_1", "get_weather", new Dictionary<string, object?> { ["city"] = "Prague" })]))
            : new ChatResponse(new ChatMessage(ChatRole.Assistant, "It is sunny in Prague.")));
        var chat = new ChatClientBuilder(model).UseInngestFunctionInvocation().UseInngestSteps().Build();
        var weather = AIFunctionFactory.Create(
            (string city) =>
            {
                tools++;
                return $"Sunny in {city}";
            },
            "get_weather",
            "Gets the weather of a city");
        var function = Define(async context => (await chat.GetResponseAsync("Weather?", new ChatOptions { Tools = [weather] })).Text);

        var result = await _host.CallCheckpointedAsync(function, Call.New());

        result.StatusCode.ShouldBe(200);
        result.Body.ShouldBe("\"It is sunny in Prague.\"");
        (model.CallCount, tools).ShouldBe((2, 1));

        // One request to Inngest for each step, in the order they ran, and none for a replay.
        var saved = _host.Checkpoints.Checkpoints
            .Select(checkpoint => JsonDocument.Parse(checkpoint.Steps).RootElement[0].GetProperty("id").GetString())
            .ToArray();
        saved.ShouldBe([Call.Hash("chat"), Call.Hash("tool get_weather"), Call.Hash("chat:1")]);
    }
}
