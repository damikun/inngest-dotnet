using System.Buffers;
using System.Text;
using System.Text.Json;
using Inngest.Json;

namespace Inngest.Tests.Internal;

// The expected values are what Inngest's server (gowebpki/jcs, as vendored in v1.52.0) makes of the same input.
public sealed class JsonCanonicalizerTests
{
    [Theory]
    [InlineData("1234567890123456789", "1234567890123456800")]
    [InlineData("19.90", "19.9")]
    [InlineData("1e21", "1e+21")]
    [InlineData("1E21", "1e+21")]
    [InlineData("1e-7", "1e-7")]
    [InlineData("-0", "0")]
    [InlineData("-0.0", "0")]
    [InlineData("0.1234567890123456789", "0.12345678901234568")]
    [InlineData("1e-6", "0.000001")]
    [InlineData("0.000001", "0.000001")]
    [InlineData("1e20", "100000000000000000000")]
    [InlineData("123456789012345680000", "123456789012345680000")]
    [InlineData("5e-324", "5e-324")]
    [InlineData("1.7976931348623157e308", "1.7976931348623157e+308")]
    [InlineData("9007199254740993", "9007199254740992")]
    [InlineData("0.30000000000000004", "0.30000000000000004")]
    [InlineData("636000000000000001", "636000000000000000")]
    [InlineData("1152921504606846976", "1152921504606847000")]
    [InlineData("18446744073709551615", "18446744073709552000")]
    [InlineData("79228162514264337593543950335", "7.922816251426434e+28")]
    [InlineData("100.00", "100")]
    [InlineData("1.0", "1")]
    [InlineData("0.0000001", "1e-7")]
    [InlineData("1.5e3", "1500")]
    [InlineData("-1234.5678", "-1234.5678")]
    [InlineData("333333333.33333329", "333333333.3333333")]
    [InlineData("1e23", "1e+23")]
    [InlineData("9.999999999999997e22", "9.999999999999997e+22")]
    [InlineData("999999999999999700000", "999999999999999700000")]
    [InlineData("-0.0000033333333333333333", "-0.0000033333333333333333")]
    [InlineData("1424953923781206.2", "1424953923781206.2")]
    [InlineData("295147905179352830000", "295147905179352830000")]
    [InlineData("4.35", "4.35")]
    [InlineData("2.5e-8", "2.5e-8")]
    [InlineData("123e-2", "1.23")]
    public void Numbers_are_written_as_Inngest_writes_them(string number, string expected)
    {
        Canonical($"[{number}]").ShouldBe($"[{expected}]");
    }

    // Inngest fails the whole call on such a number, so there is nothing to match; it is left alone.
    [Fact]
    public void A_number_beyond_the_range_of_a_double_is_left_as_it_is()
    {
        Canonical("[1e400]").ShouldBe("[1e400]");
    }

    [Fact]
    public void Keys_are_sorted_at_every_depth()
    {
        Canonical("""{"b":1,"a":{"d":[3,{"z":1,"y":2}],"c":"x"}}""").ShouldBe("""{"a":{"c":"x","d":[3,{"y":2,"z":1}]},"b":1}""");
        Canonical("""{"aB":1,"Ab":2,"a":3,"":4,"ab":5}""").ShouldBe("""{"":4,"Ab":2,"a":3,"aB":1,"ab":5}""");
    }

    // The example of RFC 8785 section 3.2.3: the emoji's surrogates sort before U+FB33, as UTF-16 code units do.
    [Fact]
    public void Keys_are_sorted_by_their_UTF_16_code_units()
    {
        const string Json =
            """{"\u20ac":"Euro Sign","\r":"Carriage Return","\ufb33":"Hebrew Letter Dalet With Dagesh","1":"One","\ud83d\ude00":"Emoji: Grinning Face","\u0080":"Control","\u00f6":"Latin Small Letter O With Diaeresis"}""";

        Canonical(Json).ShouldBe(
            "{\"\\r\":\"Carriage Return\",\"1\":\"One\",\"\u0080\":\"Control\",\"\u00f6\":\"Latin Small Letter O With Diaeresis\"," +
            "\"\u20ac\":\"Euro Sign\",\"\U0001F600\":\"Emoji: Grinning Face\",\"\ufb33\":\"Hebrew Letter Dalet With Dagesh\"}");
    }

    [Fact]
    public void Strings_are_escaped_only_where_JSON_requires_it()
    {
        Canonical("""{"s":"\u0001\u001f\b\f\n\r\t\"\\\/<>&\u2028\u00e9😀","e":"\u00e9"}""")
            .ShouldBe("{\"e\":\"\u00e9\",\"s\":\"\\u0001\\u001f\\b\\f\\n\\r\\t\\\"\\\\/<>&\u2028\u00e9\U0001F600\"}");
    }

    [Fact]
    public void Literals_and_empty_containers_stay_as_they_are()
    {
        Canonical("""[ "x", true, false, null, { }, [ ] ]""").ShouldBe("""["x",true,false,null,{},[]]""");
    }

    [Fact]
    public void Canonicalize_returns_an_element_of_its_own()
    {
        JsonElement canonical;
        using (var document = JsonDocument.Parse("""{"b":19.90,"a":[1.0]}"""))
        {
            canonical = JsonCanonicalizer.Canonicalize(document.RootElement, default);
        }

        canonical.GetRawText().ShouldBe("""{"a":[1],"b":19.9}""");
    }

    // A lone surrogate cannot be canonicalized, by Inngest either, so the value stays as it was written.
    [Fact]
    public void A_string_with_a_lone_surrogate_is_left_as_it_is()
    {
        using var document = JsonDocument.Parse("""{"b":"\ud800","a":1.0}""");

        JsonCanonicalizer.Canonicalize(document.RootElement, default).GetRawText().ShouldBe("""{"b":"\ud800","a":1.0}""");
    }

    private static string Canonical(string json)
    {
        using var document = JsonDocument.Parse(json);
        var output = new ArrayBufferWriter<byte>();
        JsonCanonicalizer.Write(document.RootElement, output);
        return Encoding.UTF8.GetString(output.WrittenSpan);
    }
}
