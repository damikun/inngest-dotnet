using System.Text.Json;
using Inngest.Tests.Execution;
using Microsoft.Extensions.DependencyInjection;

namespace Inngest.Tests.Middleware;

// TransformStepInput sees the "opts" of a step's op before Inngest gets it, as transformStepInput does in the
// TypeScript SDK, which the encryption middleware uses for the data of an invoke.
public sealed class StepInputTransformTests
{
    private readonly List<string> _log = [];

    [Fact]
    public async Task The_options_of_an_invoke_reach_inngest_as_the_transform_wrote_them()
    {
        using var host = new EngineHost(services => services.AddSingleton<InngestMiddleware>(new Tagging("only", _log)));
        var function = TestFunction.Define(async context =>
            await context.Step.InvokeAsync<int>("charge", new InvokeOptions("billing") { Data = new { amount = 5 } }));

        var result = await host.CallAsync(function, Call.New());

        result.OpCodes.ShouldBe(["InvokeFunction"]);
        var opts = result.Op().GetProperty("opts");
        opts.GetProperty("payload").GetProperty("data").GetRawText().ShouldBe("""{"amount":5}""");
        opts.GetProperty("tagged").GetString().ShouldBe("only");
        _log.ShouldBe(["only:charge Invoke"]);
    }

    [Fact]
    public async Task Outgoing_input_meets_the_transforms_in_reverse_order()
    {
        using var host = new EngineHost(services =>
        {
            services.AddSingleton<InngestMiddleware>(new Tagging("outer", _log));
            services.AddSingleton<InngestMiddleware>(new Tagging("inner", _log));
        });
        var function = TestFunction.Define(async context =>
            await context.Step.WaitForEventAsync("approval", new WaitForEventOptions("app/approved", TimeSpan.FromHours(1))));

        var result = await host.CallAsync(function, Call.New());

        result.Op().GetProperty("opts").GetProperty("tagged").GetString().ShouldBe("outer");
        _log.ShouldBe(["inner:approval WaitForEvent", "outer:approval WaitForEvent"]);
    }

    [Fact]
    public async Task A_step_that_runs_in_the_call_passes_its_input_too()
    {
        using var host = new EngineHost(services => services.AddSingleton<InngestMiddleware>(new Tagging("only", _log)));
        var function = TestFunction.Define(async context => await context.Step.RunAsync("a", () => "A"));

        var result = await host.CallAsync(function, Call.New());

        result.OpCodes.ShouldBe(["StepRun"]);
        result.Op().GetProperty("opts").GetProperty("type").GetString().ShouldBe("step.run");
        result.Op().GetProperty("opts").GetProperty("tagged").GetString().ShouldBe("only");
    }

    [Fact]
    public async Task Memoized_steps_are_not_passed()
    {
        using var host = new EngineHost(services => services.AddSingleton<InngestMiddleware>(new Tagging("only", _log)));
        var function = TestFunction.Define(async context =>
        {
            await context.Step.InvokeAsync<int>("charge", new InvokeOptions("billing"));
            return await context.Step.RunAsync("after", () => "done");
        });

        await host.CallAsync(function, Call.New().Data("charge", "1"));

        _log.ShouldBe(["only:after Run"]);
    }

    [Fact]
    public async Task A_transform_that_keeps_the_input_leaves_the_op_unchanged()
    {
        var function = TestFunction.Define(async context =>
            await context.Step.InvokeAsync<int>("charge", new InvokeOptions("billing") { Data = new { note = "plain \"text\" <b>" }, Timeout = TimeSpan.FromMinutes(5) }));
        using var plain = new EngineHost();
        using var kept = new EngineHost(services => services.AddSingleton<InngestMiddleware>(new Keeping()));

        var expected = await plain.CallAsync(function, Call.New());
        var actual = await kept.CallAsync(function, Call.New());

        actual.Body.ShouldBe(expected.Body);
    }

    [Fact]
    public async Task Input_that_is_not_an_object_fails_the_call()
    {
        using var host = new EngineHost(services => services.AddSingleton<InngestMiddleware>(new NotAnObject()));
        var function = TestFunction.Define(async context => await context.Step.InvokeAsync<int>("charge", new InvokeOptions("billing")));

        var result = await host.CallAsync(function, Call.New());

        result.StatusCode.ShouldBe(500);
        using var body = JsonDocument.Parse(result.Body);
        body.RootElement.GetProperty("message").GetString()
            .ShouldBe("TransformStepInput wrote String for step \"charge\", but a step's input is a JSON object.");
    }

    // Adds "tagged": "<name>" to the input, logging the steps it sees.
    private sealed class Tagging(string name, List<string> log) : InngestMiddleware, IPayloadTransform
    {
        public bool TransformStepInput(FunctionContext context, StepInfo step, JsonElement input, Utf8JsonWriter writer)
        {
            step.Memoized.ShouldBeFalse();
            log.Add($"{name}:{step.Id} {step.Type}");
            writer.WriteStartObject();
            foreach (var property in input.EnumerateObject())
            {
                if (!property.NameEquals("tagged"))
                {
                    property.WriteTo(writer);
                }
            }

            writer.WriteString("tagged", name);
            writer.WriteEndObject();
            return true;
        }
    }

    private sealed class Keeping : InngestMiddleware, IPayloadTransform
    {
        public bool TransformStepInput(FunctionContext context, StepInfo step, JsonElement input, Utf8JsonWriter writer) => false;
    }

    private sealed class NotAnObject : InngestMiddleware, IPayloadTransform
    {
        public bool TransformStepInput(FunctionContext context, StepInfo step, JsonElement input, Utf8JsonWriter writer)
        {
            writer.WriteStringValue("nope");
            return true;
        }
    }
}
