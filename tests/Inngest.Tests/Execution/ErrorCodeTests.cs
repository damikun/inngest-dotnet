using System.Text.Json;
using Inngest.Errors;
using Inngest.Internal;

namespace Inngest.Tests.Execution;

// inngest/inngest-js#1726: an error's code is written as "code", as the TypeScript SDK writes and reads it, and under
// "data", which Inngest keeps with the error of a step where it drops "code".
public sealed class ErrorCodeTests : IDisposable
{
    private readonly EngineHost _host = new();

    public void Dispose() => _host.Dispose();

    [Fact]
    public void The_code_of_an_exception_that_has_one_is_written_after_the_stack()
    {
        var json = Write(new PaymentDeclined("card_declined"));

        json.GetProperty("code").GetString().ShouldBe("card_declined");
        json.GetProperty("data").GetRawText().ShouldBe("""{"code":"card_declined"}""");
        json.EnumerateObject().Select(static property => property.Name).ShouldBe(["name", "message", "stack", "code", "data"]);
    }

    [Fact]
    public void A_code_in_the_data_of_an_exception_is_written()
    {
        var exception = new InvalidOperationException("declined");
        exception.Data["code"] = "card_declined";

        Write(exception).GetProperty("code").GetString().ShouldBe("card_declined");
    }

    [Fact]
    public void A_code_in_the_data_that_is_a_number_is_written_as_text()
    {
        var exception = new InvalidOperationException("declined");
        exception.Data["code"] = 4711;

        Write(exception).GetProperty("code").GetString().ShouldBe("4711");
    }

    [Fact]
    public void The_interface_wins_over_the_data()
    {
        var exception = new PaymentDeclined("card_declined");
        exception.Data["code"] = "other";

        Write(exception).GetProperty("code").GetString().ShouldBe("card_declined");
    }

    [Fact]
    public void An_exception_without_a_code_is_written_as_before()
    {
        Write(new PaymentDeclined(null)).EnumerateObject().Select(static property => property.Name).ShouldBe(["name", "message", "stack"]);
        Write(new InvalidOperationException("x")).EnumerateObject().Select(static property => property.Name).ShouldBe(["name", "message", "stack"]);
    }

    [Fact]
    public void Causes_keep_their_codes()
    {
        var json = Write(new InvalidOperationException("outer", new PaymentDeclined("card_declined")));

        json.TryGetProperty("code", out _).ShouldBeFalse();
        json.GetProperty("cause").GetProperty("code").GetString().ShouldBe("card_declined");
    }

    [Theory]
    [InlineData("""{"name":"E","message":"m","code":"ECONNRESET"}""", "ECONNRESET")]
    [InlineData("""{"name":"E","message":"m","code":42}""", "42")]
    [InlineData("""{"name":"E","message":"m","code":null}""", null)]
    [InlineData("""{"name":"E","message":"m","code":{"a":1}}""", null)]
    [InlineData("""{"name":"E","message":"m"}""", null)]
    [InlineData("""{"name":"E","message":"m","data":{"code":"card_declined"}}""", "card_declined")]
    [InlineData("""{"name":"E","message":"m","code":"top","data":{"code":"nested"}}""", "top")]
    [InlineData("""{"name":"E","message":"m","data":"text"}""", null)]
    public void A_recorded_code_is_read_when_it_is_text_or_a_number(string json, string? code)
    {
        using var document = JsonDocument.Parse(json);

        ErrorSerializer.Read(document.RootElement).Code.ShouldBe(code);
    }

    [Fact]
    public void A_recorded_error_is_written_back_with_its_code()
    {
        var error = new FunctionError { Name = "E", Message = "m", Code = "card_declined", Cause = new FunctionError { Message = "c", Code = "timeout" } };

        var json = Write(new StepFailedException("charge", error));

        json.GetProperty("code").GetString().ShouldBe("card_declined");
        json.GetProperty("cause").GetProperty("code").GetString().ShouldBe("timeout");
    }

    [Fact]
    public async Task A_failing_step_sends_its_code_to_inngest()
    {
        var function = TestFunction.Define(async context =>
            await context.Step.RunAsync<int>("charge", Throw));

        var result = await _host.CallAsync(function, Call.New());

        using var body = JsonDocument.Parse(result.Body);
        body.RootElement[0].GetProperty("error").GetProperty("code").GetString().ShouldBe("card_declined");
    }

    [Fact]
    public async Task A_step_failure_raised_in_a_later_call_has_the_code()
    {
        StepFailedException? caught = null;
        var function = TestFunction.Define(async context =>
        {
            try
            {
                await context.Step.RunAsync("charge", () => 1);
            }
            catch (StepFailedException exception)
            {
                caught = exception;
            }

            return "handled";
        });

        await _host.CallAsync(
            function,
            Call.New().Memo("charge", """{"error":{"name":"PaymentDeclined","message":"The card was declined.","code":"card_declined"}}"""));

        caught.ShouldNotBeNull().Error.Code.ShouldBe("card_declined");
    }

    private static int Throw() => throw new PaymentDeclined("card_declined");

    private static JsonElement Write(Exception exception)
    {
        using var buffer = new PooledBufferWriter();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            ErrorSerializer.Write(writer, exception);
        }

        return JsonDocument.Parse(buffer.WrittenMemory.ToArray()).RootElement;
    }

    private sealed class PaymentDeclined(string? code) : Exception("The card was declined."), IHasErrorCode
    {
        public string? Code { get; } = code;
    }
}
