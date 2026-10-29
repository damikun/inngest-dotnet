using System.Text.Json;
using Inngest.Errors;
using Inngest.Internal;

namespace Inngest.Tests.Execution;

// What Inngest stores and shows for an error: the cases of the TypeScript SDK's serializeError tests that apply to
// exceptions, plus the way an error comes back out of a memoized step.
public sealed class ErrorSerializationTests
{
    [Fact]
    public void A_custom_exception_is_written_under_its_own_type_name()
    {
        var json = Write(new PaymentDeclinedException("card declined"));

        json.GetProperty("name").GetString().ShouldBe("PaymentDeclinedException");
        json.GetProperty("message").GetString().ShouldBe("card declined");
    }

    [Fact]
    public void The_exceptions_of_the_sdk_are_written_under_their_own_names()
    {
        Write(new NonRetriableException("bad input")).GetProperty("name").GetString().ShouldBe("NonRetriableException");
        Write(new RetryAfterException("slow down", TimeSpan.FromSeconds(5))).GetProperty("name").GetString().ShouldBe("RetryAfterException");
    }

    [Fact]
    public void An_exception_that_was_never_thrown_still_has_a_stack_field()
    {
        var json = Write(new InvalidOperationException("never thrown"));

        json.TryGetProperty("stack", out var stack).ShouldBeTrue();
        stack.GetString().ShouldBe(string.Empty);
    }

    [Fact]
    public void An_exception_without_a_message_still_has_one()
    {
        Write(new Exception()).GetProperty("message").GetString().ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public void A_step_failure_is_written_as_the_error_the_step_failed_with_and_not_wrapped_again()
    {
        var failure = new StepFailedException("charge", new FunctionError { Name = "PaymentError", Message = "declined", Stack = "at Charge()" });

        var json = Write(failure);

        json.GetRawText().ShouldBe("""{"name":"PaymentError","message":"declined","stack":"at Charge()"}""");
    }

    [Fact]
    public void A_step_failure_without_a_stack_is_written_with_an_empty_one()
    {
        var failure = new StepFailedException("charge", new FunctionError { Name = "PaymentError", Message = "declined" });

        Write(failure).GetProperty("stack").GetString().ShouldBe(string.Empty);
    }

    [Fact]
    public void A_failure_of_the_sdk_itself_is_marked_internal()
    {
        using var buffer = new PooledBufferWriter();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            ErrorSerializer.WriteInternal(writer, new InvalidOperationException("unreadable body"));
        }

        var json = JsonDocument.Parse(buffer.WrittenMemory.ToArray()).RootElement;

        json.GetProperty("type").GetString().ShouldBe("internal");
        json.GetProperty("name").GetString().ShouldBe("InvalidOperationException");
        json.GetProperty("message").GetString().ShouldBe("unreadable body");
    }

    [Fact]
    public void An_error_keeps_its_name_message_and_stack_through_the_wire_format()
    {
        Exception thrown;
        try
        {
            throw new PaymentDeclinedException("card declined");
        }
        catch (Exception exception)
        {
            thrown = exception;
        }

        var error = ErrorSerializer.Read(Write(thrown));

        error.Name.ShouldBe("PaymentDeclinedException");
        error.Message.ShouldBe("card declined");
        error.Stack.ShouldNotBeNullOrEmpty();
        error.Stack.ShouldContain(nameof(An_error_keeps_its_name_message_and_stack_through_the_wire_format));
    }

    [Fact]
    public void An_error_read_back_from_a_step_is_raised_with_the_original_message_and_the_step_id()
    {
        var error = ErrorSerializer.Read(Write(new NonRetriableException("bad input")));

        var failure = new StepFailedException("validate", error);

        failure.Message.ShouldBe("bad input");
        failure.StepId.ShouldBe("validate");
        failure.Error.Name.ShouldBe("NonRetriableException");
    }

    private static JsonElement Write(Exception error)
    {
        using var buffer = new PooledBufferWriter();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            ErrorSerializer.Write(writer, error);
        }

        return JsonDocument.Parse(buffer.WrittenMemory.ToArray()).RootElement;
    }

    private sealed class PaymentDeclinedException(string message) : Exception(message);
}
