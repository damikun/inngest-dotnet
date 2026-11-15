using System.Text;
using Inngest.Json;

namespace Inngest.Tests.Internal;

public sealed class NumberPrecisionTests
{
    [Theory]
    [InlineData("1234567890123456789")]
    [InlineData("9007199254740993")]
    [InlineData("636000000000000001")]
    [InlineData("1152921504606846976")]
    [InlineData("-9223372036854775808")]
    [InlineData("18446744073709551615")]
    [InlineData("0.1234567890123456789")]
    [InlineData("79228162514264337593543950335")]
    [InlineData("19.90")]
    [InlineData("100.00")]
    [InlineData("1.0")]
    [InlineData("1e400")]
    public void Numbers_Inngest_hands_back_changed_are_found(string number)
    {
        NumberPrecision.Changes(Encoding.UTF8.GetBytes(number)).ShouldBeTrue();
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-0")]
    [InlineData("42")]
    [InlineData("2147483647")]
    [InlineData("9007199254740992")]
    [InlineData("-9007199254740991")]
    [InlineData("1793268000000")]
    [InlineData("0.30000000000000004")]
    [InlineData("12345.6789")]
    [InlineData("19.9")]
    [InlineData("1E+21")]
    [InlineData("1e-7")]
    [InlineData("1.5e3")]
    [InlineData("100000000000000000000")]
    [InlineData("1.7976931348623157E+308")]
    public void Numbers_Inngest_hands_back_with_the_same_value_are_not(string number)
    {
        NumberPrecision.Changes(Encoding.UTF8.GetBytes(number)).ShouldBeFalse();
    }

    [Theory]
    [InlineData("1234567890123456789", "$")]
    [InlineData("""{"orders":[{"id":1},{"id":1234567890123456789}]}""", "$.orders[1].id")]
    [InlineData("""{"a":{"b":[[1,2],[3,19.90]]}}""", "$.a.b[1][1]")]
    [InlineData("""{"line items":[{"price":1.50}]}""", "$['line items'][0].price")]
    [InlineData("""[{"x":"1234567890123456789"},{"$id":7,"y":[{},1234567890123456789]}]""", "$[1].y[1]")]
    public void The_path_of_the_first_changed_number_is_given(string json, string path)
    {
        NumberPrecision.FindChanged(Encoding.UTF8.GetBytes(json), 64).ShouldBe(path);
    }

    [Theory]
    [InlineData("""{"id":"1234567890123456789","amount":"19.90","count":3,"ratio":0.25}""")]
    [InlineData("[]")]
    [InlineData("null")]
    public void Json_without_such_numbers_has_no_path(string json)
    {
        NumberPrecision.FindChanged(Encoding.UTF8.GetBytes(json), 64).ShouldBeNull();
    }
}
