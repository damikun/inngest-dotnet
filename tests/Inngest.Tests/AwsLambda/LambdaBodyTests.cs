using System.Security.Cryptography;
using System.Text;
using Inngest.AwsLambda;

namespace Inngest.Tests.AwsLambda;

public sealed class LambdaBodyTests
{
    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData(null, true)]
    [InlineData("", true)]
    public void No_body_is_no_buffer(string? body, bool isBase64Encoded)
    {
        LambdaBody.Decode(body, isBase64Encoded).ShouldBeNull();
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"name":"Zoë 😀 日本語"}""")]
    [InlineData("a")]
    public void Text_is_its_UTF_8_bytes(string body)
    {
        using var buffer = LambdaBody.Decode(body, isBase64Encoded: false);

        buffer.ShouldNotBeNull().WrittenMemory.ToArray().ShouldBe(Encoding.UTF8.GetBytes(body));
    }

    [Theory]
    [InlineData("")]
    [InlineData("a")]
    [InlineData("ab")]
    [InlineData("abc")]
    [InlineData("abcd")]
    [InlineData("""{"name":"Zoë 😀 日本語"}""")]
    public void Base64_is_the_bytes_it_decodes_to_for_every_padding(string text)
    {
        if (text.Length == 0)
        {
            return;
        }

        using var buffer = LambdaBody.Decode(Convert.ToBase64String(Encoding.UTF8.GetBytes(text)), isBase64Encoded: true);

        buffer.ShouldNotBeNull().WrittenMemory.ToArray().ShouldBe(Encoding.UTF8.GetBytes(text));
    }

    [Fact]
    public void Base64_may_hold_bytes_that_are_not_text()
    {
        var bytes = RandomNumberGenerator.GetBytes(1000);

        using var buffer = LambdaBody.Decode(Convert.ToBase64String(bytes), isBase64Encoded: true);

        buffer.ShouldNotBeNull().WrittenMemory.ToArray().ShouldBe(bytes);
    }

    [Theory]
    [InlineData(1_000_000, false)]
    [InlineData(1_000_000, true)]
    public void Large_bodies_arrive_intact(int size, bool isBase64Encoded)
    {
        var bytes = RandomNumberGenerator.GetBytes(size);
        var body = isBase64Encoded ? Convert.ToBase64String(bytes) : new string('x', size);

        using var buffer = LambdaBody.Decode(body, isBase64Encoded);

        var expected = isBase64Encoded ? bytes : Encoding.UTF8.GetBytes(body);
        SHA256.HashData(buffer.ShouldNotBeNull().WrittenSpan).ShouldBe(SHA256.HashData(expected));
    }

    [Theory]
    [InlineData("not base64!")]
    [InlineData("abc")]
    [InlineData("a")]
    public void A_base64_body_that_is_not_base64_is_refused(string body)
    {
        var exception = Should.Throw<FormatException>(() => LambdaBody.Decode(body, isBase64Encoded: true));

        exception.Message.ShouldContain("base64");
    }
}
