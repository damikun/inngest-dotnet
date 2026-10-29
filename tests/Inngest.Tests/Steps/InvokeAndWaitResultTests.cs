using System.Text.Json;
using Inngest.Tests.Execution;

namespace Inngest.Tests.Steps;

// What the function gets back from the steps that Inngest carries out, and how the options of those steps are
// written, in the cases the TypeScript SDK's step tool tests add to the ones in StepOpTests.
public sealed class InvokeAndWaitResultTests : IDisposable
{
    private readonly EngineHost _host = new();

    public void Dispose() => _host.Dispose();

    [Fact]
    public async Task An_invoked_function_without_output_gives_null()
    {
        var function = TestFunction.Define(async context =>
        {
            var text = await context.Step.InvokeAsync<string?>("invoke", new InvokeOptions("child"));
            return text is null ? "null" : "value";
        });

        var result = await _host.CallAsync(function, Call.New().Data("invoke", "null"));

        result.Body.ShouldBe("\"null\"");
    }

    [Fact]
    public async Task An_invoked_function_without_output_is_a_json_null_for_an_untyped_result()
    {
        var function = TestFunction.Define(async context =>
        {
            var output = await context.Step.InvokeAsync<JsonElement>("invoke", new InvokeOptions("child"));
            return output.ValueKind.ToString();
        });

        var result = await _host.CallAsync(function, Call.New().Data("invoke", "null"));

        result.Body.ShouldBe("\"Null\"");
    }

    [Fact]
    public async Task An_invoked_function_returns_what_it_returned_in_the_declared_type()
    {
        var function = TestFunction.Define(async context =>
        {
            var output = await context.Step.InvokeAsync<Receipt>("invoke", new InvokeOptions("child"));
            return $"{output.Number}:{output.Amount}";
        });

        var result = await _host.CallAsync(function, Call.New().Data("invoke", """{"number":"R-1","amount":42}"""));

        result.Body.ShouldBe("\"R-1:42\"");
    }

    [Fact]
    public async Task A_wait_for_event_with_both_a_match_and_an_expression_applies_both()
    {
        var function = TestFunction.Define(async context =>
        {
            await context.Step.WaitForEventAsync("wait", new WaitForEventOptions("app/paid", TimeSpan.FromMinutes(1))
            {
                Match = "data.orderId",
                If = "async.data.total > 100",
            });
            return null;
        });

        var result = await _host.CallAsync(function, Call.New());

        result.Op().GetProperty("opts").GetProperty("if").GetString()
            .ShouldBe("event.data.orderId == async.data.orderId && (async.data.total > 100)");
    }

    [Fact]
    public async Task A_wait_for_event_that_matched_returns_the_event_with_its_data()
    {
        var function = TestFunction.Define(async context =>
        {
            var paid = await context.Step.WaitForEventAsync<Paid>("wait", new WaitForEventOptions("app/paid", TimeSpan.FromMinutes(1)));
            return $"{paid!.Name}:{paid.Data.OrderId}";
        });

        var result = await _host.CallAsync(function, Call.New().Memo("wait", """{"name":"app/paid","data":{"orderId":"o-9"},"id":"01JA","ts":1793268000000}"""));

        result.Body.ShouldBe("\"app/paid:o-9\"");
    }

    internal sealed record Receipt(string Number, int Amount);

    internal sealed record Paid(string OrderId);
}
