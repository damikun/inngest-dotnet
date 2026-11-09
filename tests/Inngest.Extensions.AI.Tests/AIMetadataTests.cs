using System.Text.Json;
using Inngest.Tests.Execution;
using Microsoft.Extensions.AI;

namespace Inngest.Extensions.AI.Tests;

// The usage that OpenTelemetryChatClient records for a model call reaches the op of the step the call is made in.
public sealed class AIMetadataTests : IDisposable
{
    private readonly EngineHost _host = new();

    public void Dispose() => _host.Dispose();

    [Fact]
    public async Task A_model_step_carries_the_usage_of_its_call()
    {
        var chat = new ChatClientBuilder(new FakeChatClient(Respond)).UseInngestSteps().UseOpenTelemetry().Build();

        var result = await _host.CallAsync(Function(async () => (await chat.GetResponseAsync("Say hello", new ChatOptions { ModelId = "gpt-4o-mini", Temperature = 0.2f })).Text), Call.New());

        result.OpCodes.ShouldBe(["StepRun"]);
        var values = Values(result);
        values.GetProperty("operation_name").GetString().ShouldBe("chat");
        values.GetProperty("request_model").GetString().ShouldBe("gpt-4o-mini");
        values.GetProperty("response_model").GetString().ShouldBe("gpt-4o-mini-2024-07-18");
        values.GetProperty("response_id").GetString().ShouldBe("chatcmpl-1");
        values.GetProperty("finish_reasons").EnumerateArray().Select(reason => reason.GetString()).ShouldBe(["stop"]);
        values.GetProperty("input_tokens").GetInt32().ShouldBe(12);
        values.GetProperty("output_tokens").GetInt32().ShouldBe(4);
        values.GetProperty("cache_read_tokens").GetInt32().ShouldBe(8);
        values.GetProperty("temperature").GetDouble().ShouldBe(0.2);
    }

    [Fact]
    public async Task A_streaming_step_carries_the_usage_of_its_stream()
    {
        var chat = new ChatClientBuilder(new FakeChatClient(Respond)).UseInngestSteps().UseOpenTelemetry().Build();

        var result = await _host.CallAsync(
            Function(async () => (await chat.GetStreamingResponseAsync("Say hello").ToChatResponseAsync()).Text),
            Call.New());

        Values(result).GetProperty("output_tokens").GetInt32().ShouldBe(4);
    }

    // The activity is then around the step, in the function, where nothing is collected.
    [Fact]
    public async Task OpenTelemetry_outside_the_steps_stage_records_nothing_on_the_step()
    {
        var chat = new ChatClientBuilder(new FakeChatClient(Respond)).UseOpenTelemetry().UseInngestSteps().Build();

        var result = await _host.CallAsync(Function(async () => (await chat.GetResponseAsync("Say hello")).Text), Call.New());

        result.OpCodes.ShouldBe(["StepRun"]);
        result.Op().TryGetProperty("metadata", out _).ShouldBeFalse();
    }

    private static ChatResponse Respond(int call, IList<ChatMessage> messages) =>
        new(new ChatMessage(ChatRole.Assistant, "Hello there"))
        {
            ResponseId = "chatcmpl-1",
            ModelId = "gpt-4o-mini-2024-07-18",
            FinishReason = ChatFinishReason.Stop,
            Usage = new UsageDetails { InputTokenCount = 12, OutputTokenCount = 4, TotalTokenCount = 16, CachedInputTokenCount = 8 },
        };

    private static Functions.FunctionDefinition Function(Func<Task<string>> call) =>
        TestFunction.Define(async _ => await call());

    private static JsonElement Values(CallResult result)
    {
        var metadata = result.Op().GetProperty("metadata");
        metadata.GetArrayLength().ShouldBe(1);
        metadata[0].GetProperty("kind").GetString().ShouldBe("inngest.ai");
        return metadata[0].GetProperty("values");
    }
}
