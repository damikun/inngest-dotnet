using System.Globalization;
using System.Text.Json;
using Inngest.Errors;
using Inngest.Internal;
using Inngest.Tests.Execution;

namespace Inngest.Tests.Steps;

// A step error keeps its cause chain, as StepError.cause does in the TypeScript SDK: it is read into FunctionError,
// raised as the inner exceptions of StepFailedException and written back unchanged when the function rethrows it.
public sealed class StepFailureCauseTests : IDisposable
{
    private const string PaymentError =
        """{"name":"PaymentError","message":"declined","stack":"at Charge()","cause":{"name":"TimeoutException","message":"gateway timed out","stack":"at Gateway()"}}""";

    private readonly EngineHost _host = new();

    public void Dispose() => _host.Dispose();

    [Fact]
    public void Causes_are_read_level_by_level()
    {
        var error = Read("""{"name":"A","message":"a","cause":{"name":"B","message":"b","stack":"at B()","cause":{"message":"c"}}}""");

        error.ShouldBe(new FunctionError
        {
            Name = "A",
            Message = "a",
            Cause = new FunctionError { Name = "B", Message = "b", Stack = "at B()", Cause = new FunctionError { Message = "c" } },
        });
    }

    [Theory]
    [InlineData("\"card expired\"", "Error", "card expired")]
    [InlineData("""{"error":"from the error field"}""", "Error", "from the error field")]
    [InlineData("42", "Error", "Unknown error")]
    public void A_cause_that_is_not_an_error_object_gets_the_same_defaults_as_an_error(string cause, string name, string message)
    {
        var error = Read($$"""{"name":"E","message":"m","cause":{{cause}}}""");

        error.Cause.ShouldBe(new FunctionError { Name = name, Message = message });
    }

    [Theory]
    [InlineData("""{"message":"m"}""")]
    [InlineData("""{"message":"m","cause":null}""")]
    public void An_error_without_a_cause_has_none(string json) => Read(json).Cause.ShouldBeNull();

    [Fact]
    public void Causes_are_read_up_to_the_five_levels_that_are_written()
    {
        var json = """{"message":"0"}""";
        for (var i = 1; i <= 8; i++)
        {
            json = $$"""{"message":"{{i.ToString(CultureInfo.InvariantCulture)}}","cause":{{json}}}""";
        }

        var depth = 0;
        for (var error = Read(json).Cause; error is not null; error = error.Cause)
        {
            depth++;
        }

        depth.ShouldBe(5);
    }

    [Fact]
    public void The_inner_exceptions_of_a_step_failure_are_its_causes()
    {
        var failure = new StepFailedException("charge", Read(PaymentError));

        var cause = failure.InnerException.ShouldBeOfType<FunctionErrorException>();
        cause.Message.ShouldBe("gateway timed out");
        cause.StackTrace.ShouldBe("at Gateway()");
        cause.Error.ShouldBeSameAs(failure.Error.Cause);
        cause.InnerException.ShouldBeNull();
        new StepFailedException("charge", new FunctionError { Message = "no cause" }).InnerException.ShouldBeNull();
    }

    [Fact]
    public void A_rebuilt_cause_reports_the_recorded_type_name_message_and_stack()
    {
        var error = new FunctionError
        {
            Name = "TypeError",
            Message = "x is undefined",
            Stack = "TypeError: x is undefined\n    at f (index.js:1:1)",
            Cause = new FunctionError { Name = "RangeError", Message = "too big" },
        };

        var text = new FunctionErrorException(error).ToString();

        text.ShouldStartWith("TypeError: x is undefined ---> RangeError: too big");
        text.ShouldContain("--- End of inner exception stack trace ---");
        text.ShouldEndWith("at f (index.js:1:1)");
    }

    [Fact]
    public void A_step_failure_is_written_back_with_its_whole_cause_chain() =>
        Write(new StepFailedException("charge", Read(PaymentError))).GetRawText().ShouldBe(PaymentError);

    [Fact]
    public void A_rebuilt_cause_is_written_as_the_error_it_was_read_from() =>
        Write(new InvalidOperationException("wrapped", new FunctionErrorException(Read(PaymentError)))).GetProperty("cause").GetRawText().ShouldBe(PaymentError);

    [Fact]
    public void A_cause_chain_is_written_up_to_five_levels()
    {
        var error = new FunctionError { Message = "0" };
        for (var i = 1; i <= 8; i++)
        {
            error = new FunctionError { Message = i.ToString(CultureInfo.InvariantCulture), Cause = error };
        }

        var json = Write(new StepFailedException("deep", error));

        var depth = 0;
        while (json.TryGetProperty("cause", out var cause))
        {
            json = cause;
            depth++;
        }

        depth.ShouldBe(5);
    }

    [Fact]
    public async Task A_caught_step_failure_exposes_its_cause()
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

        var result = await _host.CallAsync(function, Call.New().Memo("charge", $$"""{"error":{{PaymentError}}}"""));

        result.StatusCode.ShouldBe(200);
        caught.ShouldNotBeNull().Error.Cause.ShouldNotBeNull().Name.ShouldBe("TimeoutException");
        caught.InnerException.ShouldBeOfType<FunctionErrorException>().Message.ShouldBe("gateway timed out");
    }

    [Fact]
    public async Task A_step_failure_the_function_does_not_catch_fails_the_run_with_the_whole_chain()
    {
        var function = TestFunction.Define(async context => await context.Step.RunAsync("charge", () => 1));

        var result = await _host.CallAsync(function, Call.New().Memo("charge", $$"""{"error":{{PaymentError}}}"""));

        result.StatusCode.ShouldBe(400);
        result.Body.ShouldBe(PaymentError);
    }

    private static FunctionError Read(string json)
    {
        using var document = JsonDocument.Parse(json);
        return ErrorSerializer.Read(document.RootElement);
    }

    private static JsonElement Write(Exception exception)
    {
        using var buffer = new PooledBufferWriter();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            ErrorSerializer.Write(writer, exception);
        }

        return JsonDocument.Parse(buffer.WrittenMemory.ToArray()).RootElement;
    }
}
