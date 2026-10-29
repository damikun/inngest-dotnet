using System.Text.Json;
using Inngest.Tests.Execution;
using Microsoft.Extensions.DependencyInjection;
using static Inngest.Tests.Checkpointing.Checkpointed;

namespace Inngest.Tests.Checkpointing;

// Checkpointing next to the features that were built alongside it: payload transforms and race mode.
public sealed class CheckpointingInteractionTests
{
    [Fact]
    public async Task A_checkpointed_step_is_saved_transformed_and_handed_back_as_the_handler_returned_it()
    {
        using var host = new EngineHost(services => services.AddSingleton<InngestMiddleware>(new Wrapping()));
        var seen = new List<string>();
        var function = Define(async context =>
        {
            var first = await context.Step.RunAsync("A", () => "a");
            seen.Add(first);
            var second = await context.Step.RunAsync("B", () => first + "b");
            seen.Add(second);
            return second;
        });

        var result = await host.CallCheckpointedAsync(function, Call.New());

        result.StatusCode.ShouldBe(200);
        seen.ShouldBe(["a", "ab"]);
        host.Checkpoints.Checkpoints
            .Select(checkpoint => checkpoint.Ops.ShouldHaveSingleItem().GetProperty("data").GetRawText())
            .ShouldBe(["""{"wrapped":"a"}""", """{"wrapped":"ab"}"""]);
    }

    [Fact]
    public async Task A_lone_step_of_a_race_is_planned_rather_than_run_in_the_call()
    {
        using var host = new EngineHost();
        var ran = false;
        var function = Define(async context =>
            await context.Step.RunAsync(new StepOptions("A") { ParallelMode = ParallelMode.Race }, () =>
            {
                ran = true;
                return 1;
            }));

        var result = await host.CallCheckpointedAsync(function, Call.New());

        result.StatusCode.ShouldBe(206);
        ran.ShouldBeFalse();
        var op = result.Op();
        op.GetProperty("op").GetString().ShouldBe("StepPlanned");
        op.GetProperty("opts").GetProperty("parallelMode").GetString().ShouldBe("race");
        host.Checkpoints.Checkpoints.ShouldBeEmpty();
    }

    // Stores step output as {"wrapped": <output>} and unwraps it when it comes back.
    private sealed class Wrapping : InngestMiddleware, IPayloadTransform
    {
        public bool TransformStepResult(FunctionContext context, StepInfo step, JsonElement result, Utf8JsonWriter writer)
        {
            if (result.ValueKind != JsonValueKind.Object || !result.TryGetProperty("wrapped", out var inner))
            {
                return false;
            }

            inner.WriteTo(writer);
            return true;
        }

        public bool TransformStepOutput(FunctionContext context, StepInfo step, JsonElement output, Utf8JsonWriter writer)
        {
            writer.WriteStartObject();
            writer.WritePropertyName("wrapped");
            output.WriteTo(writer);
            writer.WriteEndObject();
            return true;
        }
    }
}
