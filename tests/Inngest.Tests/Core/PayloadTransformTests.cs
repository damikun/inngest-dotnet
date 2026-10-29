using System.Text;
using System.Text.Json;
using Inngest.Execution;
using Inngest.Functions;
using Inngest.Tests.Execution;
using Microsoft.Extensions.DependencyInjection;

namespace Inngest.Tests.Core;

// Middleware that implements IPayloadTransform sees the raw JSON of events, memoized step results, step output and
// function output, which is what the TypeScript SDK's encryption middleware does with its transform and wrap hooks.
public sealed class PayloadTransformTests
{
    private readonly List<string> _log = [];

    [Fact]
    public async Task Event_data_is_opened_before_the_function_reads_it_while_the_raw_event_stays_as_sent()
    {
        using var host = new EngineHost(services => services.AddSingleton<InngestMiddleware>(new Sealing()));
        Secret? data = null;
        var raw = default(JsonElement);
        var function = TestFunction.Define<Secret>(context =>
        {
            data = context.Event.Data;
            raw = context.RawEvent;
            return Task.FromResult<object?>(null);
        });

        await host.CallAsync(function, Call.New().Event(SealedEvent("""{"value":"s3cret"}""")));

        data.ShouldBe(new Secret("s3cret"));
        raw.GetProperty("data").GetProperty("__ENCRYPTED__").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task Every_event_of_a_batch_is_opened_once()
    {
        using var host = new EngineHost(services => services.AddSingleton<InngestMiddleware>(new Sealing(_log)));
        IReadOnlyList<InngestEvent<Secret>>? events = null;
        var function = TestFunction.Define<Secret>(context =>
        {
            events = context.Events;
            return Task.FromResult<object?>(null);
        });

        await host.CallAsync(function, Call.New().Events(SealedEvent("""{"value":"one"}"""), SealedEvent("""{"value":"two"}""")));

        events.ShouldNotBeNull().Select(@event => @event.Data.Value).ShouldBe(["one", "two"]);
        _log.Count(entry => entry == "event").ShouldBe(2);
    }

    [Fact]
    public async Task Step_output_is_sealed_on_its_way_to_inngest_and_opened_when_it_is_replayed()
    {
        using var host = new EngineHost(services => services.AddSingleton<InngestMiddleware>(new Sealing()));
        string? seen = null;
        var function = TestFunction.Define(async context =>
        {
            seen = await context.Step.RunAsync("token", () => "s3cret");
            return new Secret(seen);
        });

        var first = await host.CallAsync(function, Call.New());
        var stored = first.Op().GetProperty("data").GetRawText();
        var second = await host.CallAsync(function, Call.New().Data("token", stored));

        first.OpCodes.ShouldBe(["StepRun"]);
        stored.ShouldBe(Seal("\"s3cret\""));
        seen.ShouldBe("s3cret");
        second.StatusCode.ShouldBe(200);
        second.Body.ShouldBe(Seal("""{"value":"s3cret"}"""));
    }

    [Fact]
    public async Task Incoming_json_meets_the_transforms_in_registration_order_and_outgoing_json_in_reverse()
    {
        using var host = new EngineHost(services =>
        {
            services.AddSingleton<InngestMiddleware>(new Tagging("outer", _log));
            services.AddSingleton<InngestMiddleware>(new Tagging("inner", _log));
        });
        var function = TestFunction.Define(async context => await context.Step.RunAsync("a", () => "A") + "!");

        var first = await host.CallAsync(function, Call.New());
        _log.Add("--");
        var last = await host.CallAsync(function, Call.New().Data("a", first.Op().GetProperty("data").GetRawText()));

        first.Op().GetProperty("data").GetRawText().ShouldBe("""{"outer":{"inner":"A"}}""");
        last.Body.ShouldBe("""{"outer":{"inner":"A!"}}""");
        _log.ShouldBe(
        [
            "outer:event", "inner:event", "inner:step a", "outer:step a", "--",
            "outer:event", "inner:event", "outer:result a", "inner:result a", "inner:output", "outer:output",
        ]);
    }

    [Fact]
    public async Task Every_memoized_result_passes_through_with_its_step_type()
    {
        using var host = new EngineHost(services => services.AddSingleton<InngestMiddleware>(new Tagging("only", _log)));
        var function = TestFunction.Define(async context =>
        {
            await context.Step.SleepAsync("nap", TimeSpan.FromSeconds(1));
            var matched = await context.Step.WaitForEventAsync("approval", new WaitForEventOptions("app/approved", TimeSpan.FromHours(1)));
            return matched?.Name;
        });

        var result = await host.CallAsync(function, Call.New().Memo("nap", "null").Memo("approval", """{"name":"app/approved","data":{}}"""));

        result.Body.ShouldBe("""{"only":"app/approved"}""");
        _log.ShouldContain("only:result nap Sleep");
        _log.ShouldContain("only:result approval WaitForEvent");
    }

    [Fact]
    public async Task A_transform_that_keeps_every_value_leaves_the_wire_format_unchanged()
    {
        var function = TestFunction.Define(async context => await context.Step.RunAsync("a", () => new Secret("plain \"text\" <b>")));
        using var plain = new EngineHost();
        using var kept = new EngineHost(services => services.AddSingleton<InngestMiddleware>(new Keeping(_log)));

        var expected = await plain.CallAsync(function, Call.New());
        var actual = await kept.CallAsync(function, Call.New());

        actual.Body.ShouldBe(expected.Body);
        _log.ShouldBe(["event", "step output"]);
    }

    [Fact]
    public async Task A_transform_that_writes_and_returns_false_fails_the_step()
    {
        using var host = new EngineHost(services => services.AddSingleton<InngestMiddleware>(new Broken(writeButKeep: true)));
        var function = TestFunction.Define(async context => await context.Step.RunAsync("a", () => "A"));

        var result = await host.CallAsync(function, Call.New());

        result.OpCodes.ShouldBe(["StepError"]);
        result.Op().GetProperty("error").GetProperty("message").GetString()
            .ShouldBe("Broken.TransformStepOutput wrote a value but returned false. Return true to use what it wrote.");
    }

    [Fact]
    public async Task A_transform_that_returns_true_without_a_value_fails_the_run()
    {
        using var host = new EngineHost(services => services.AddSingleton<InngestMiddleware>(new Broken(writeButKeep: false)));

        var result = await host.CallAsync(TestFunction.Define(_ => Task.FromResult<object?>("done")), Call.New());

        result.StatusCode.ShouldBe(500);
        result.Json.GetProperty("message").GetString().ShouldBe("Broken.TransformFunctionOutput returned true without writing a complete JSON value.");
    }

    [Fact]
    public async Task A_function_middleware_transforms_only_its_functions_payloads()
    {
        using var host = new EngineHost();
        var sealedFunction = new FunctionDefinition(
            new FunctionOptions { Id = "sealed", Triggers = [Trigger.Event("test/event")], Middleware = [FunctionMiddleware.Of(_ => new Sealing())] },
            _ => new DelegateFunction<JsonElement>((_, _) => Task.FromResult<object?>("s3cret")));

        var sealedResult = await host.CallAsync(sealedFunction, Call.New());
        var plainResult = await host.CallAsync(TestFunction.Define(_ => Task.FromResult<object?>("s3cret")), Call.New());

        sealedResult.Body.ShouldBe(Seal("\"s3cret\""));
        plainResult.Body.ShouldBe("\"s3cret\"");
    }

    [Fact]
    public void A_call_whose_middleware_does_not_transform_payloads_gets_no_transforms()
    {
        using var host = new EngineHost(services => services.AddSingleton<InngestMiddleware>(new Observing()));
        using var document = JsonDocument.Parse(Call.New().ToBytes());
        var request = new ExecutionRequest { Function = TestFunction.Define(_ => Task.FromResult<object?>(null)), Body = Call.New().ToBytes(), Services = host.Services };

        var invocation = new Invocation(host.Engine, request, RunRequest.Parse(document.RootElement), CancellationToken.None);

        invocation.Transforms.ShouldBeNull();
    }

    private static string Seal(string json) =>
        $$"""{"__ENCRYPTED__":true,"data":"{{Convert.ToBase64String(Encoding.UTF8.GetBytes(json))}}"}""";

    private static string SealedEvent(string data) =>
        $$"""{"name":"test/event","data":{{Seal(data)}},"id":"01JSEALED","ts":1793268000000}""";

    private sealed record Secret(string Value);

    // A reversible stand-in for encryption: base64 of the JSON inside an envelope shaped like the TypeScript SDK's.
    private sealed class Sealing(List<string>? log = null) : InngestMiddleware, IPayloadTransform
    {
        public bool TransformEvent(JsonElement @event, Utf8JsonWriter writer)
        {
            log?.Add("event");
            if (!@event.TryGetProperty("data", out var data) || !IsSealed(data))
            {
                return false;
            }

            writer.WriteStartObject();
            foreach (var property in @event.EnumerateObject())
            {
                if (property.NameEquals("data"))
                {
                    writer.WritePropertyName("data");
                    writer.WriteRawValue(data.GetProperty("data").GetBytesFromBase64());
                }
                else
                {
                    property.WriteTo(writer);
                }
            }

            writer.WriteEndObject();
            return true;
        }

        public bool TransformStepResult(FunctionContext context, StepInfo step, JsonElement result, Utf8JsonWriter writer)
        {
            if (!IsSealed(result))
            {
                return false;
            }

            writer.WriteRawValue(result.GetProperty("data").GetBytesFromBase64());
            return true;
        }

        public bool TransformStepOutput(FunctionContext context, StepInfo step, JsonElement output, Utf8JsonWriter writer) => SealInto(output, writer);

        public bool TransformFunctionOutput(FunctionContext context, JsonElement output, Utf8JsonWriter writer) => SealInto(output, writer);

        private static bool IsSealed(JsonElement value) =>
            value.ValueKind == JsonValueKind.Object && value.TryGetProperty("__ENCRYPTED__", out _);

        private static bool SealInto(JsonElement value, Utf8JsonWriter writer)
        {
            writer.WriteStartObject();
            writer.WriteBoolean("__ENCRYPTED__", true);
            writer.WriteBase64String("data", Encoding.UTF8.GetBytes(value.GetRawText()));
            writer.WriteEndObject();
            return true;
        }
    }

    // Wraps output in {"<name>": ...} and unwraps results wrapped that way, logging the order it is called in.
    private sealed class Tagging(string name, List<string> log) : InngestMiddleware, IPayloadTransform
    {
        public bool TransformEvent(JsonElement @event, Utf8JsonWriter writer)
        {
            log.Add($"{name}:event");
            return false;
        }

        public bool TransformStepResult(FunctionContext context, StepInfo step, JsonElement result, Utf8JsonWriter writer)
        {
            log.Add(step.Type == StepType.Run ? $"{name}:result {step.Id}" : $"{name}:result {step.Id} {step.Type}");
            if (result.ValueKind != JsonValueKind.Object || !result.TryGetProperty(name, out var inner))
            {
                return false;
            }

            inner.WriteTo(writer);
            return true;
        }

        public bool TransformStepOutput(FunctionContext context, StepInfo step, JsonElement output, Utf8JsonWriter writer)
        {
            log.Add($"{name}:step {step.Id}");
            return Wrap(output, writer);
        }

        public bool TransformFunctionOutput(FunctionContext context, JsonElement output, Utf8JsonWriter writer)
        {
            log.Add($"{name}:output");
            return Wrap(output, writer);
        }

        private bool Wrap(JsonElement value, Utf8JsonWriter writer)
        {
            writer.WriteStartObject();
            writer.WritePropertyName(name);
            value.WriteTo(writer);
            writer.WriteEndObject();
            return true;
        }
    }

    // Middleware that observes runs and rewrites nothing.
    private sealed class Observing : InngestMiddleware;

    private sealed class Keeping(List<string> log) : InngestMiddleware, IPayloadTransform
    {
        public bool TransformEvent(JsonElement @event, Utf8JsonWriter writer)
        {
            log.Add("event");
            return false;
        }

        public bool TransformStepResult(FunctionContext context, StepInfo step, JsonElement result, Utf8JsonWriter writer) => false;

        public bool TransformStepOutput(FunctionContext context, StepInfo step, JsonElement output, Utf8JsonWriter writer)
        {
            log.Add("step output");
            return false;
        }

        public bool TransformFunctionOutput(FunctionContext context, JsonElement output, Utf8JsonWriter writer) => false;
    }

    private sealed class Broken(bool writeButKeep) : InngestMiddleware, IPayloadTransform
    {
        public bool TransformEvent(JsonElement @event, Utf8JsonWriter writer) => false;

        public bool TransformStepResult(FunctionContext context, StepInfo step, JsonElement result, Utf8JsonWriter writer) => false;

        public bool TransformStepOutput(FunctionContext context, StepInfo step, JsonElement output, Utf8JsonWriter writer)
        {
            if (writeButKeep)
            {
                writer.WriteStringValue("replacement");
            }

            return false;
        }

        public bool TransformFunctionOutput(FunctionContext context, JsonElement output, Utf8JsonWriter writer) => !writeButKeep;
    }
}
