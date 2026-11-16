using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
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
    [InlineData("100000000000000000000", "100000000000000000000")]
    [InlineData("1000000000000000000000", "1e+21")]
    [InlineData("0.10", "0.1")]
    [InlineData("-0.5", "-0.5")]
    [InlineData("123456789012345", "123456789012345")]
    [InlineData("0.123456789012345", "0.123456789012345")]
    [InlineData("0.000001234", "0.000001234")]
    [InlineData("0.0000001234", "1.234e-7")]
    [InlineData("999999999999999.9", "999999999999999.9")]
    [InlineData("-100", "-100")]
    [InlineData("1.50", "1.5")]
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

    // The same keys written as UTF-8, which sorts U+E000 to U+FFFF before the characters beyond U+FFFF.
    [Fact]
    public void Keys_written_as_UTF_8_are_sorted_by_their_UTF_16_code_units_too()
    {
        const string Json =
            "{\"\u20ac\":1,\"\\r\":2,\"\ufb33\":3,\"1\":4,\"\U0001F600\":5,\"\u0080\":6,\"\u00f6\":7,\"\uffff\":8,\"\U00010000\":9,\"\ud7ff\":10,\"\ue000\":11}";

        Canonical(Json).ShouldBe(
            "{\"\\r\":2,\"1\":4,\"\u0080\":6,\"\u00f6\":7,\"\u20ac\":1,\"\ud7ff\":10,\"\U00010000\":9,\"\U0001F600\":5," +
            "\"\ue000\":11,\"\ufb33\":3,\"\uffff\":8}");
    }

    [Fact]
    public void Escaped_keys_are_sorted_by_the_characters_they_stand_for()
    {
        Canonical("""{"\u0062":1,"a":2,"\n":3,"\u0001":4,"\u00e9":5,"\u00E8":6,"\/":7}""")
            .ShouldBe("{\"\\u0001\":4,\"\\n\":3,\"/\":7,\"a\":2,\"b\":1,\"\u00e8\":6,\"\u00e9\":5}");
    }

    // Inngest refuses an object with a key twice; the SDK keeps both, in the order they came.
    [Fact]
    public void A_key_that_comes_twice_keeps_its_order()
    {
        Canonical("""{"b":1,"a":1,"b":2,"c":0,"b":3}""").ShouldBe("""{"a":1,"b":1,"b":2,"b":3,"c":0}""");
    }

    [Fact]
    public void An_object_with_many_keys_is_sorted()
    {
        var keys = Enumerable.Range(0, 300).Select(static i => $"key{i * 7919 % 300}").ToList();
        var json = "{" + string.Join(",", keys.Select(static (key, i) => $"\"{key}\":{i}")) + "}";

        var sorted = keys.Select(static (key, i) => (key, i)).OrderBy(static member => member.key, StringComparer.Ordinal);
        Canonical(json).ShouldBe("{" + string.Join(",", sorted.Select(static member => $"\"{member.key}\":{member.i}")) + "}");
    }

    // What System.Text.Json's encoders escape, which Inngest writes as the characters themselves.
    [Fact]
    public void Escapes_of_the_serializer_become_the_characters()
    {
        Canonical("""["\u003C\u003E\u0026\u0027\u0022\u002B\u0060\u007F\u0080\u00E9\u2028\u2029\uD83D\uDE00\uFEFF\u005C\/\u001F\u0008\u000a\u0041"]""")
            .ShouldBe("[\"<>&'\\\"+`\u007f\u0080\u00e9\u2028\u2029\U0001F600\ufeff\\\\/\\u001f\\b\\nA\"]");
    }

    [Theory]
    [InlineData("""{"a":[1,-2,0.5,1e+21,1e-7,"x\n\u001f\"\\é"],"b":{"":null,"c":true},"d":false}""")]
    [InlineData("\"\"")]
    [InlineData("123456789012345")]
    [InlineData("[1e400]")]
    [InlineData("{\"\u00e9\":1,\"\U0001F600\":2,\"\ufb33\":3}")]
    public void A_value_in_canonical_form_is_written_as_it_is(string json)
    {
        Canonical(json).ShouldBe(json);
        JsonCanonicalizer.Canonicalize(Encoding.UTF8.GetBytes(json), default).GetRawText().ShouldBe(json);
    }

    [Theory]
    [InlineData("""[1E2]""", "[100]")]
    [InlineData("""[1.0]""", "[1]")]
    [InlineData("""[-0]""", "[0]")]
    [InlineData("""[1234567890123456]""", "[1234567890123456]")]
    [InlineData("""[12345678901234567]""", "[12345678901234568]")]
    [InlineData("""["\/"]""", """["/"]""")]
    [InlineData("""["\u001F"]""", """["\u001f"]""")]
    [InlineData("""["\u0009"]""", """["\t"]""")]
    [InlineData("""["\u0041"]""", """["A"]""")]
    [InlineData("""{"b":1,"a":2}""", """{"a":2,"b":1}""")]
    [InlineData("""{"a":{"c":1,"b":2}}""", """{"a":{"b":2,"c":1}}""")]
    [InlineData("""[ 1]""", "[1]")]
    [InlineData("""[1 ]""", "[1]")]
    [InlineData(""" 1""", "1")]
    [InlineData("""1 """, "1")]
    [InlineData("""{"a" :1}""", """{"a":1}""")]
    [InlineData("""{"a": 1}""", """{"a":1}""")]
    public void Every_departure_from_the_canonical_form_is_rewritten(string json, string expected)
    {
        Canonical(json).ShouldBe(expected);
        JsonCanonicalizer.Canonicalize(Encoding.UTF8.GetBytes(json), default).GetRawText().ShouldBe(expected);
    }

    // Values of every shape, written by the serializer's encoders and by hand, against a canonicalizer that reads
    // every name and string as a .NET string.
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void Random_values_are_written_as_a_plain_canonicalizer_writes_them(int seed)
    {
        var random = new Random(seed);
        for (var i = 0; i < 200; i++)
        {
            var json = RandomJson(random, depth: 0);
            using var document = JsonDocument.Parse(json);
            foreach (var encoder in new[] { JavaScriptEncoder.Default, JavaScriptEncoder.UnsafeRelaxedJsonEscaping })
            {
                var written = new ArrayBufferWriter<byte>();
                using (var writer = new Utf8JsonWriter(written, new JsonWriterOptions { Encoder = encoder }))
                {
                    document.RootElement.WriteTo(writer);
                }

                var expected = PlainCanonical(document.RootElement);
                Canonical(Encoding.UTF8.GetString(written.WrittenSpan)).ShouldBe(expected, json);
            }

            Canonical(json).ShouldBe(PlainCanonical(document.RootElement), json);
            Canonical(Plain(document.RootElement)).ShouldBe(PlainCanonical(document.RootElement), json);
        }
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
        var json = Encoding.UTF8.GetBytes("""{"b":19.90,"a":[1.0]}""");

        var canonical = JsonCanonicalizer.Canonicalize(json, default);
        json.AsSpan().Clear();

        canonical.GetRawText().ShouldBe("""{"a":[1],"b":19.9}""");
    }

    [Fact]
    public void Canonicalize_returns_a_canonical_value_as_its_own_element_too()
    {
        var json = Encoding.UTF8.GetBytes("""{"a":[1],"b":19.9}""");

        var canonical = JsonCanonicalizer.Canonicalize(json, default);
        json.AsSpan().Clear();

        canonical.GetRawText().ShouldBe("""{"a":[1],"b":19.9}""");
    }

    // A lone surrogate cannot be canonicalized, by Inngest either, so the value stays as it was written.
    [Theory]
    [InlineData("""{"b":"\ud800","a":1.0}""")]
    [InlineData("""{"b":"\udc00x","a":1.0}""")]
    [InlineData("""{"b":"\ud800\u0041","a":1.0}""")]
    [InlineData("""{"\ud800":1,"a":1.0}""")]
    public void A_string_with_a_lone_surrogate_is_left_as_it_is(string json)
    {
        JsonCanonicalizer.Canonicalize(Encoding.UTF8.GetBytes(json), default).GetRawText().ShouldBe(json);
        Should.Throw<InvalidOperationException>(() => Canonical(json));
    }

    // Bytes that are not UTF-8 cannot be read as a string, so the value stays as it was written.
    [Fact]
    public void A_string_that_is_not_UTF_8_is_left_as_it_is()
    {
        byte[] json = [.. """{"b":" """u8, 0xC3, .. """ ","a":1.0}"""u8];

        var canonical = JsonCanonicalizer.Canonicalize(json, default);

        canonical.EnumerateObject().Select(property => property.Name).ShouldBe(["b", "a"]);
        canonical.GetProperty("a").GetRawText().ShouldBe("1.0");
        Should.Throw<InvalidOperationException>(() => JsonCanonicalizer.Write(json, new ArrayBufferWriter<byte>()));
    }

    private static readonly string[] Keys =
        ["", "a", "aa", "ab", "B", "\u00e9", "\u00e8", "\u20ac", "\ufb33", "\uffff", "\ue000", "\U0001F600", "\U00010000", "\n", "\u0001", "\"", "\\", "/", "<&>", "\u2028"];

    private static readonly string[] Numbers =
        ["0", "-0", "1", "-1", "19.90", "1e21", "1E-7", "0.1", "1234567890123456789", "4.35", "123e-2", "1e400", "-1.5e3", "100.00", "0.000001", "0.0000001", "100000000000000000000", "1000000000000000000000"];

    private static string RandomJson(Random random, int depth)
    {
        switch (random.Next(depth > 3 ? 4 : 6))
        {
            case 0:
                return random.GetItems(Numbers, 1)[0];
            case 1:
                return JsonSerializer.Serialize(string.Concat(random.GetItems(Keys, random.Next(4))));
            case 2:
                return random.GetItems(["true", "false", "null"], 1)[0];
            case 3:
                return JsonSerializer.Serialize(new string((char)random.Next(0x20), 1) + "x");
            case 4:
                return "[" + string.Join(random.Next(2) == 0 ? "," : " , ", Enumerable.Range(0, random.Next(5)).Select(_ => RandomJson(random, depth + 1))) + "]";
            default:
                var keys = random.GetItems(Keys, random.Next(12));
                return "{" + string.Join(",", keys.Select(key => $"{JsonSerializer.Serialize(key)}:{RandomJson(random, depth + 1)}")) + "}";
        }
    }

    // The canonicalizer as it first was, reading every name and string as a .NET string.
    private static string PlainCanonical(JsonElement value) => Plain(value, canonical: true);

    // Without sorting or numbers changed, every string is written as UTF-8, characters beyond U+FFFF too.
    private static string Plain(JsonElement value, bool canonical = false)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                IEnumerable<JsonProperty> properties = value.EnumerateObject();
                return "{" + string.Join(",", (canonical ? properties.OrderBy(static property => property.Name, StringComparer.Ordinal) : properties)
                    .Select(property => PlainString(property.Name) + ":" + Plain(property.Value, canonical))) + "}";
            case JsonValueKind.Array:
                return "[" + string.Join(",", value.EnumerateArray().Select(item => Plain(item, canonical))) + "]";
            case JsonValueKind.String:
                return PlainString(value.GetString()!);
            case JsonValueKind.Number:
                var raw = value.GetRawText();
                if (!canonical || !double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) || !double.IsFinite(number))
                {
                    return raw;
                }

                var digits = new byte[32];
                return Encoding.UTF8.GetString(digits, 0, JsonCanonicalizer.FormatNumber(number, digits));
            default:
                return value.GetRawText();
        }
    }

    private static string PlainString(string value)
    {
        var builder = new StringBuilder("\"");
        foreach (var c in value)
        {
            builder.Append(c switch
            {
                '"' => "\\\"",
                '\\' => "\\\\",
                '\b' => "\\b",
                '\f' => "\\f",
                '\n' => "\\n",
                '\r' => "\\r",
                '\t' => "\\t",
                < ' ' => $"\\u{(int)c:x4}",
                _ => c.ToString(),
            });
        }

        return builder.Append('"').ToString();
    }

    private static string Canonical(string json)
    {
        var output = new ArrayBufferWriter<byte>();
        JsonCanonicalizer.Write(Encoding.UTF8.GetBytes(json), output);
        return Encoding.UTF8.GetString(output.WrittenSpan);
    }
}
