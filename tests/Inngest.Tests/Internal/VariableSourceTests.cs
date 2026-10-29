using Inngest.Internal;

namespace Inngest.Tests.Internal;

public sealed class VariableSourceTests
{
    // The table of the TypeScript SDK's parseAsBoolean: only the words and digits of a flag are one, so a dev
    // server address in the same variable is never mistaken for "false".
    [Theory]
    [InlineData("true", true)]
    [InlineData("TRUE", true)]
    [InlineData("True", true)]
    [InlineData("1", true)]
    [InlineData("false", false)]
    [InlineData("FALSE", false)]
    [InlineData("0", false)]
    [InlineData(" true ", true)]
    [InlineData("http://localhost:3000", null)]
    [InlineData("0.0.0.0:8288", null)]
    [InlineData("localhost:9000", null)]
    [InlineData("random string", null)]
    [InlineData("undefined", null)]
    [InlineData("2", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void A_flag_is_true_false_or_neither(string? value, bool? expected) =>
        VariableSource.ParseBoolean(value).ShouldBe(expected);

    [Fact]
    public void Blank_values_count_as_unset_and_the_rest_is_trimmed()
    {
        var variables = new VariableSource(name => name switch
        {
            "BLANK" => "   ",
            "PADDED" => "  value  ",
            _ => null,
        });

        variables.Get("BLANK").ShouldBeNull();
        variables.Get("PADDED").ShouldBe("value");
        variables.Get("MISSING").ShouldBeNull();
    }

    [Fact]
    public void The_first_name_that_is_set_wins()
    {
        var variables = new VariableSource(name => name switch
        {
            "SECOND" => "two",
            "THIRD" => "three",
            _ => null,
        });

        variables.GetFirst(["FIRST", "SECOND", "THIRD"]).ShouldBe("two");
        variables.GetFirst(["FIRST"]).ShouldBeNull();
    }
}
