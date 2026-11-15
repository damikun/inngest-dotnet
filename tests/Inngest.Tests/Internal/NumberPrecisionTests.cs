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
    [InlineData("0.12345678901234567890")]
    [InlineData("1234567890123456789.00")]
    [InlineData("1e400")]
    public void Numbers_Inngest_hands_back_with_another_value_are_found(string number)
    {
        NumberPrecision.Change(Encoding.UTF8.GetBytes(number)).ShouldBe(NumberChange.Value);
    }

    [Theory]
    [InlineData("19.90")]
    [InlineData("100.00")]
    [InlineData("1.0")]
    [InlineData("-0.0")]
    [InlineData("0.0000001000")]
    [InlineData("1234567890123456.000000000000000000")]
    public void Numbers_that_only_lose_the_trailing_zeros_of_their_fraction_are_found_apart(string number)
    {
        NumberPrecision.Change(Encoding.UTF8.GetBytes(number)).ShouldBe(NumberChange.TrailingZeros);
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
    [InlineData("1.50e3")]
    public void Numbers_Inngest_hands_back_with_the_same_value_are_not(string number)
    {
        NumberPrecision.Change(Encoding.UTF8.GetBytes(number)).ShouldBe(NumberChange.None);
    }

    [Theory]
    [InlineData("1234567890123456789", "$")]
    [InlineData("""{"orders":[{"id":1},{"id":1234567890123456789}]}""", "$.orders[1].id")]
    [InlineData("""{"a":{"b":[[1,2],[3,0.12345678901234567890]]}}""", "$.a.b[1][1]")]
    [InlineData("""{"line items":[{"price":1.5,"sku":1234567890123456789}]}""", "$['line items'][0].sku")]
    [InlineData("""[{"x":"1234567890123456789"},{"$id":7,"y":[{},1234567890123456789]}]""", "$[1].y[1]")]
    [InlineData("""{"price":19.90,"id":1234567890123456789}""", "$.id")]
    public void The_path_of_the_first_number_whose_value_changes_is_given(string json, string path)
    {
        NumberPrecision.FindChanged(Encoding.UTF8.GetBytes(json), 64, trailingZeros: true).ShouldBe((path, NumberChange.Value));
    }

    [Theory]
    [InlineData("""{"a":{"b":[[1,2],[3,19.90]]}}""", "$.a.b[1][1]")]
    [InlineData("""{"line items":[{"price":1.50},{"price":2.0}]}""", "$['line items'][0].price")]
    public void Without_such_a_number_the_path_of_the_first_that_loses_trailing_zeros_is_given(string json, string path)
    {
        NumberPrecision.FindChanged(Encoding.UTF8.GetBytes(json), 64, trailingZeros: true).ShouldBe((path, NumberChange.TrailingZeros));
    }

    [Fact]
    public void Trailing_zeros_are_only_looked_for_when_asked()
    {
        NumberPrecision.FindChanged("""{"price":19.90}"""u8, 64, trailingZeros: false).ShouldBeNull();
    }

    [Theory]
    [InlineData("""{"id":"1234567890123456789","amount":"19.90","count":3,"ratio":0.25}""")]
    [InlineData("[]")]
    [InlineData("null")]
    public void Json_without_such_numbers_has_no_path(string json)
    {
        NumberPrecision.FindChanged(Encoding.UTF8.GetBytes(json), 64, trailingZeros: true).ShouldBeNull();
    }
}
