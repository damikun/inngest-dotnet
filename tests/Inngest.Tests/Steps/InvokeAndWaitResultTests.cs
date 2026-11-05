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

    // Inngest memoizes the whole inngest/function.finished event when the invoked run is cancelled (inngest-js#1694).
    private const string CancelledRun = """
        {"id":"01KZMH3G9VEC5FW3SPH723PJR2","name":"inngest/function.finished","ts":1786321682747,"user":{},
         "data":{"_inngest":{"status":"Cancelled"},"correlation_id":"01KZMH2MWC1TKB1MAFKVKB2PWB.cf3c","function_id":"test-app-child",
                 "run_id":"01KZMH2MWC1TKB1MAFKVKB2PWC","event":{"name":"inngest/function.invoked","data":{}}}}
        """;

    [Theory]
    [InlineData(CancelledRun)]
    [InlineData($$"""{"data":{{CancelledRun}}}""")]
    public async Task An_invoked_function_that_was_cancelled_fails_the_step_instead_of_returning_the_finished_event(string memo)
    {
        var function = TestFunction.Define(async context =>
        {
            try
            {
                await context.Step.InvokeAsync<Receipt>("invoke", new InvokeOptions("child"));
                return "returned";
            }
            catch (StepFailedException exception)
            {
                return $"{exception.StepId}|{exception.Error.Name}|{exception.Message}";
            }
        });

        var result = await _host.CallAsync(function, Call.New().Memo("invoke", memo));

        result.StatusCode.ShouldBe(200);
        result.Json.GetString().ShouldBe("invoke|NonRetriableException|The invoked function \"test-app-child\" was cancelled.");
    }

    [Fact]
    public async Task An_uncaught_cancelled_invoke_fails_the_run_without_retries()
    {
        var function = TestFunction.Define(async context => await context.Step.InvokeAsync<JsonElement>("invoke", new InvokeOptions("child")));

        var result = await _host.CallAsync(function, Call.New().Memo("invoke", CancelledRun));

        result.StatusCode.ShouldBe(400);
        result.NoRetry.ShouldBe(true);
    }

    [Fact]
    public async Task An_invoked_function_whose_output_mentions_cancelled_but_is_no_finished_event_returns_it()
    {
        var function = TestFunction.Define(async context =>
        {
            var output = await context.Step.InvokeAsync<JsonElement>("invoke", new InvokeOptions("child"));
            return output.GetProperty("_inngest").GetProperty("status").GetString();
        });

        var result = await _host.CallAsync(function, Call.New().Data("invoke", """{"_inngest":{"status":"Cancelled"}}"""));

        result.Body.ShouldBe("\"Cancelled\"");
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
