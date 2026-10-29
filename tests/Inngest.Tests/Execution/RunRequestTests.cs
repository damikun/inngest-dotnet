using System.Text.Json;
using Inngest.Execution;

namespace Inngest.Tests.Execution;

public sealed class RunRequestTests
{
    [Fact]
    public void Missing_fields_get_defaults()
    {
        using var document = JsonDocument.Parse("""{"event":{"name":"e"}}""");

        var request = RunRequest.Parse(document.RootElement);

        request.RunId.ShouldBe(string.Empty);
        request.Attempt.ShouldBe(0);
        request.MaxAttempts.ShouldBeNull();
        request.DisableImmediateExecution.ShouldBeFalse();
        request.UseApi.ShouldBeFalse();
        request.Events.ShouldBeEmpty();
        request.Memo.ShouldBeEmpty();
        request.Order.ShouldBeEmpty();
        request.EventName.ShouldBe("e");
    }

    [Fact]
    public void The_order_follows_the_stack_then_the_remaining_results()
    {
        using var document = JsonDocument.Parse(
            """{"steps":{"a":{"data":1},"b":null,"c":{"input":[]},"d":{"error":{"name":"E","message":"m"}}},"ctx":{"stack":{"stack":["d","b","missing","d"]}}}""");

        var request = RunRequest.Parse(document.RootElement);

        request.Order.ShouldBe(["d", "b", "a"]);
        request.Memo["c"].Kind.ShouldBe(MemoKind.Input);
    }

    [Fact]
    public void The_deprecated_top_level_use_api_flag_is_honoured()
    {
        using var document = JsonDocument.Parse("""{"use_api":true,"ctx":{"use_api":false}}""");

        RunRequest.Parse(document.RootElement).UseApi.ShouldBeTrue();
    }

    [Theory]
    [InlineData("""{"data":"x"}""", "Data", "\"x\"")]
    [InlineData("""{"data":null}""", "Data", "null")]
    [InlineData("""{"type":"data","data":5}""", "Data", "5")]
    [InlineData("""{"error":{"name":"E","message":"m"}}""", "Error", """{"name":"E","message":"m"}""")]
    [InlineData("""{"input":[1,2]}""", "Input", "[1,2]")]
    [InlineData("null", "Data", "null")]
    [InlineData("42", "Data", "42")]
    [InlineData("{}", "Data", "{}")]
    [InlineData("""{"name":"app/paid","data":{"id":1},"ts":1}""", "Data", """{"name":"app/paid","data":{"id":1},"ts":1}""")]
    [InlineData("""{"data":1,"other":2}""", "Data", """{"data":1,"other":2}""")]
    [InlineData("""{"type":"error","data":1}""", "Data", """{"type":"error","data":1}""")]
    public void Memoized_values_are_read_like_the_typescript_sdk_reads_them(string json, string kind, string value)
    {
        using var document = JsonDocument.Parse(json);

        var entry = MemoEntry.Parse(document.RootElement);

        entry.Kind.ShouldBe(Enum.Parse<MemoKind>(kind));
        entry.Value.GetRawText().ShouldBe(value);
    }
}
