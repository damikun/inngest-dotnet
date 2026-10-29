using System.Globalization;
using System.Text.Json;
using Inngest.Errors;
using Inngest.Internal;

namespace Inngest.Tests.Execution;

public sealed class ErrorSerializerTests
{
    [Fact]
    public void Errors_are_written_with_name_message_stack_and_cause()
    {
        Exception error;
        try
        {
            throw new InvalidOperationException("outer", new FormatException("inner"));
        }
        catch (Exception exception)
        {
            error = exception;
        }

        var json = Write(error);

        json.GetProperty("name").GetString().ShouldBe("InvalidOperationException");
        json.GetProperty("message").GetString().ShouldBe("outer");
        json.GetProperty("stack").GetString()!.ShouldContain(nameof(Errors_are_written_with_name_message_stack_and_cause));
        json.GetProperty("cause").GetRawText().ShouldBe("""{"name":"FormatException","message":"inner","stack":""}""");
    }

    [Fact]
    public void Causes_stop_after_five_levels()
    {
        Exception error = new InvalidOperationException("0");
        for (var i = 1; i <= 8; i++)
        {
            error = new InvalidOperationException(i.ToString(CultureInfo.InvariantCulture), error);
        }

        var json = Write(error);

        var depth = 0;
        while (json.TryGetProperty("cause", out var cause))
        {
            json = cause;
            depth++;
        }

        depth.ShouldBe(5);
    }

    [Theory]
    [InlineData("""{"name":"TypeError","message":"x is undefined","stack":"TypeError: x"}""", "TypeError", "x is undefined", "TypeError: x")]
    [InlineData("""{"message":"no name"}""", "Error", "no name", null)]
    [InlineData("""{"name":"E","error":"from error field"}""", "E", "from error field", null)]
    [InlineData("""{}""", "Error", "Unknown error", null)]
    [InlineData("\"plain text\"", "Error", "plain text", null)]
    [InlineData("42", "Error", "Unknown error", null)]
    public void Stored_errors_are_read_with_the_typescript_defaults(string json, string name, string message, string? stack)
    {
        using var document = JsonDocument.Parse(json);

        var error = ErrorSerializer.Read(document.RootElement);

        error.Name.ShouldBe(name);
        error.Message.ShouldBe(message);
        error.Stack.ShouldBe(stack);
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
}
