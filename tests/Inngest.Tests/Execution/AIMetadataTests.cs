using System.Diagnostics;
using System.Text.Json;
using Inngest.Tests.Checkpointing;

namespace Inngest.Tests.Execution;

// The "inngest.ai" metadata that a step's op carries for the GenAI activities its handler stops, as inngest-js sends it.
// The activities here are made the way OpenTelemetryChatClient of Microsoft.Extensions.AI makes them, under its source.
public sealed class AIMetadataTests : IDisposable
{
    private static readonly ActivitySource ChatSource = new("Experimental.Microsoft.Extensions.AI");

    private readonly EngineHost _host = new();

    public void Dispose() => _host.Dispose();

    [Fact]
    public async Task A_model_call_in_a_step_is_sent_with_the_step_as_inngest_ai_metadata()
    {
        var result = await RunStep(() => Chat(input: 12, output: 5, finishReason: "stop"));

        var metadata = result.Op().GetProperty("metadata");
        metadata.GetArrayLength().ShouldBe(1);
        metadata[0].GetProperty("kind").GetString().ShouldBe("inngest.ai");
        metadata[0].GetProperty("scope").GetString().ShouldBe("step");
        metadata[0].GetProperty("op").GetString().ShouldBe("set");
        WithoutLatency(metadata[0].GetProperty("values")).ShouldBe(
            """{"request_model":"gpt-4o-mini","response_model":"gpt-4o-mini-2024-07-18","provider":"openai","operation_name":"chat","response_id":"chatcmpl-1","finish_reasons":["stop"],"input_tokens":12,"output_tokens":5,"cache_read_tokens":2,"reasoning_tokens":1,"temperature":0.2,"max_tokens":100}""");
        metadata[0].GetProperty("values").GetProperty("latency_ms").GetDouble().ShouldBeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public async Task The_calls_of_one_step_add_up_their_tokens_and_keep_the_last_model()
    {
        var result = await RunStep(() =>
        {
            Chat(input: 10, output: 3, model: "gpt-4o-mini");
            Chat(input: 7, output: 4, model: "gpt-4o", finishReason: "length");
        });

        var values = result.Op().GetProperty("metadata")[0].GetProperty("values");
        values.GetProperty("input_tokens").GetInt32().ShouldBe(17);
        values.GetProperty("output_tokens").GetInt32().ShouldBe(7);
        values.GetProperty("cache_read_tokens").GetInt32().ShouldBe(4);
        values.GetProperty("request_model").GetString().ShouldBe("gpt-4o");
        values.GetProperty("finish_reasons")[0].GetString().ShouldBe("length");
    }

    // FunctionInvokingChatClient and agents put the usage of all their calls on an activity around them as well.
    [Fact]
    public async Task An_activity_around_calls_already_counted_is_not_counted_again()
    {
        var result = await RunStep(() =>
        {
            using var orchestrate = ChatSource.StartActivity("orchestrate_tools")!;
            Chat(input: 10, output: 3);
            Chat(input: 20, output: 6);
            orchestrate.SetTag("gen_ai.usage.input_tokens", 30);
            orchestrate.SetTag("gen_ai.usage.output_tokens", 9);
        });

        var values = result.Op().GetProperty("metadata")[0].GetProperty("values");
        values.GetProperty("input_tokens").GetInt32().ShouldBe(30);
        values.GetProperty("output_tokens").GetInt32().ShouldBe(9);
    }

    [Fact]
    public async Task Calls_awaited_inside_the_handler_count_and_calls_in_the_function_body_do_not()
    {
        var function = TestFunction.Define(async context =>
        {
            Chat(input: 99, output: 99);
            await context.Step.RunAsync("summarize", async () =>
            {
                await Task.Yield();
                await Task.Run(() => Chat(input: 3, output: 1));
                return "summary";
            });
            return null;
        });

        var result = await _host.CallAsync(function, Call.New());

        result.Op().GetProperty("metadata")[0].GetProperty("values").GetProperty("input_tokens").GetInt32().ShouldBe(3);
    }

    [Fact]
    public async Task A_step_without_model_calls_sends_no_metadata()
    {
        using var other = new ActivitySource("Inngest.Tests.Other");
        var result = await RunStep(() => other.StartActivity("work")?.Dispose());

        result.Op().TryGetProperty("metadata", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task A_step_that_fails_sends_what_its_calls_used()
    {
        var function = TestFunction.Define(async context =>
        {
            await context.Step.RunAsync("summarize", Fail);
            return null;

            static string Fail()
            {
                Chat(input: 4, output: 0);
                throw new InvalidOperationException("The model answered nonsense.");
            }
        });

        var result = await _host.CallAsync(function, Call.New());

        result.OpCodes.ShouldBe(["StepError"]);
        result.Op().GetProperty("metadata")[0].GetProperty("values").GetProperty("input_tokens").GetInt32().ShouldBe(4);
    }

    [Fact]
    public async Task Turned_off_it_sends_nothing()
    {
        using var host = new EngineHost(settings: EngineHost.Settings with { AIMetadata = false });
        var function = TestFunction.Define(async context =>
        {
            await context.Step.RunAsync("summarize", () => Chat(input: 1, output: 1));
            return null;
        });

        var result = await host.CallAsync(function, Call.New());

        result.OpCodes.ShouldBe(["StepRun"]);
        result.Op().TryGetProperty("metadata", out _).ShouldBeFalse();
    }

    // Instrumentation other than Microsoft.Extensions.AI is read when the app samples it, as its OpenTelemetry does.
    [Fact]
    public async Task Another_source_counts_when_the_app_listens_to_it()
    {
        using var source = new ActivitySource("Inngest.Tests.OtherGenAI");
        using var listener = new ActivityListener
        {
            ShouldListenTo = candidate => candidate == source,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
        };
        ActivitySource.AddActivityListener(listener);

        var result = await RunStep(() =>
        {
            using var activity = source.StartActivity("chat claude")!;
            activity.SetTag("gen_ai.system", "anthropic");
            activity.SetTag("gen_ai.usage.input_tokens", "8");
            activity.SetTag("gen_ai.response.finish_reasons", new[] { "end_turn" });
        });

        WithoutLatency(result.Op().GetProperty("metadata")[0].GetProperty("values"))
            .ShouldBe("""{"provider":"anthropic","finish_reasons":["end_turn"],"input_tokens":8}""");
    }

    [Fact]
    public async Task A_checkpointed_step_carries_its_metadata_into_the_checkpoint()
    {
        var function = Checkpointed.Define(async context =>
        {
            await context.Step.RunAsync("summarize", () => Chat(input: 6, output: 2));
            return "done";
        });

        var result = await _host.CallCheckpointedAsync(function, Call.New());

        result.Body.ShouldBe("\"done\"");
        var op = _host.Checkpoints.Checkpoints.ShouldHaveSingleItem().Ops[0];
        op.GetProperty("metadata")[0].GetProperty("values").GetProperty("output_tokens").GetInt32().ShouldBe(2);
    }

    private Task<CallResult> RunStep(Action handler) =>
        _host.CallAsync(
            TestFunction.Define(async context =>
            {
                await context.Step.RunAsync("summarize", handler);
                return null;
            }),
            Call.New());

    // What OpenTelemetryChatClient sets on an activity that some listener wants all data of.
    private static void Chat(int input, int output, string model = "gpt-4o-mini", string finishReason = "stop")
    {
        // Null outside a step, where nothing asks for the activity.
        using var activity = ChatSource.StartActivity($"chat {model}", ActivityKind.Client);
        activity?
            .AddTag("gen_ai.operation.name", "chat")
            .AddTag("gen_ai.request.model", model)
            .AddTag("gen_ai.provider.name", "openai")
            .AddTag("gen_ai.request.max_tokens", 100)
            .AddTag("gen_ai.request.temperature", 0.2f)
            .AddTag("gen_ai.response.finish_reasons", $"[\"{finishReason}\"]")
            .AddTag("gen_ai.response.id", "chatcmpl-1")
            .AddTag("gen_ai.response.model", model + "-2024-07-18")
            .AddTag("gen_ai.usage.input_tokens", input)
            .AddTag("gen_ai.usage.output_tokens", output)
            .AddTag("gen_ai.usage.cache_read.input_tokens", 2)
            .AddTag("gen_ai.usage.reasoning.output_tokens", 1);
    }

    private static string WithoutLatency(JsonElement values) =>
        JsonSerializer.Serialize(values.EnumerateObject().Where(property => property.Name != "latency_ms").ToDictionary(property => property.Name, property => property.Value));
}
