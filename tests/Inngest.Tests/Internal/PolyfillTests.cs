#if !NET9_0_OR_GREATER
extern alias connect;
#endif
using System.Buffers;
using System.Text;
using System.Text.Json;
#if NET9_0_OR_GREATER
using System.Runtime.InteropServices;
#else
using JsonMarshal = connect::System.Runtime.InteropServices.JsonMarshal;
#endif

namespace Inngest.Tests.Internal;

// The SDK calls these .NET 9 APIs unchanged on every target framework; .NET 8 gets polyfills. The tests run on both,
// so on .NET 8 they hold the polyfills to what the built-in methods do on .NET 9 and later.
public sealed class PolyfillTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(20)]
    [InlineData(32)]
    [InlineData(64)]
    [InlineData(65)]
    [InlineData(1000)]
    public void ToHexStringLower_writes_lowercase_hex(int length)
    {
        var bytes = Enumerable.Range(0, length).Select(i => (byte)(i * 37 + 11)).ToArray();

        Convert.ToHexStringLower(bytes.AsSpan()).ShouldBe(Convert.ToHexString(bytes).ToLowerInvariant());
    }

    [Theory]
    [InlineData("", 0, OperationStatus.Done, "")]
    [InlineData("", 4, OperationStatus.Done, "")]
    [InlineData("ab", 4, OperationStatus.Done, "ab")]
    [InlineData("0aFf10", 3, OperationStatus.Done, "0aff10")]
    [InlineData("0aFf10", 8, OperationStatus.Done, "0aff10")]
    [InlineData("abcdef", 2, OperationStatus.DestinationTooSmall, "abcd")]
    [InlineData("zz", 0, OperationStatus.DestinationTooSmall, "")]
    [InlineData("abc", 4, OperationStatus.NeedMoreData, "ab")]
    [InlineData("a", 4, OperationStatus.NeedMoreData, "")]
    [InlineData("abzz", 4, OperationStatus.InvalidData, "ab")]
    [InlineData("zz", 4, OperationStatus.InvalidData, "")]
    [InlineData("ab٠١", 4, OperationStatus.InvalidData, "ab")]
    public void FromHexString_reports_the_status_and_the_bytes_it_decoded(string text, int destinationLength, OperationStatus expected, string expectedHex)
    {
        var destination = new byte[destinationLength];

        var status = Convert.FromHexString(text.AsSpan(), destination, out _, out var written);

        status.ShouldBe(expected);
        Convert.ToHexStringLower(destination.AsSpan(0, written)).ShouldBe(expectedHex);
    }

    [Theory]
    [InlineData("", new[] { "" })]
    [InlineData("a", new[] { "a" })]
    [InlineData("a&b", new[] { "a", "b" })]
    [InlineData("a&&b", new[] { "a", "", "b" })]
    [InlineData("&a&", new[] { "", "a", "" })]
    [InlineData("&", new[] { "", "" })]
    public void Split_yields_the_range_of_every_piece(string text, string[] expected) =>
        Pieces(text).ShouldBe(expected);

    [Fact]
    public void GetPropertyCount_counts_only_the_properties_of_the_object_itself()
    {
        using var document = JsonDocument.Parse("""{"a":1,"b":{"c":2,"d":3},"e":[1,2,3],"f":{}}""");
        var root = document.RootElement;

        root.GetPropertyCount().ShouldBe(4);
        root.GetProperty("b").GetPropertyCount().ShouldBe(2);
        root.GetProperty("f").GetPropertyCount().ShouldBe(0);
    }

    [Fact]
    public void GetPropertyCount_requires_an_object()
    {
        using var document = JsonDocument.Parse("""{"list":[1]}""");
        var list = document.RootElement.GetProperty("list");

        Should.Throw<InvalidOperationException>(() => list.GetPropertyCount());
    }

    [Fact]
    public void GetRawUtf8Value_returns_the_json_as_it_was_written()
    {
        const string Functions = """[ {"id": "a", "name": "café ☕ ☕"} ]""";
        using var document = JsonDocument.Parse($$"""{ "functions": {{Functions}}, "capabilities": {} }""");

        var raw = JsonMarshal.GetRawUtf8Value(document.RootElement.GetProperty("functions"));

        Encoding.UTF8.GetString(raw).ShouldBe(Functions);
    }

    private static string[] Pieces(string text)
    {
        var span = text.AsSpan();
        var pieces = new List<string>();
        foreach (var range in span.Split('&'))
        {
            pieces.Add(span[range].ToString());
        }

        return [.. pieces];
    }
}
