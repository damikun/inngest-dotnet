using System.Security.Cryptography;
using System.Text;
using Inngest.Execution;

namespace Inngest.Tests.Execution;

public sealed class StepIdsTests
{
    [Theory]
    [InlineData("A", "6dcd4ce23d88e2ee9568ba546c007c63d9131c1b")]
    [InlineData("A:1", "3c782d85a10d2b0600979de846390aae2b5b50ba")]
    [InlineData("my-step-id", "e7d8a2f140845095749d60246ff1110c9d01d76a")]
    [InlineData("my-step-id:1", "f47c6228c581af870aa285200cf95ec3ac59bb6a")]
    [InlineData("complete", "0737c22d3bfae812339732d14d8c7dbd6dc4e09c")]
    public void Step_ids_are_hashed_with_lowercase_hex_sha1(string id, string expected) =>
        StepIds.Hash(id).ShouldBe(expected);

    [Fact]
    public void Long_ids_are_hashed_as_utf8()
    {
        var id = new string('ž', 300);

        StepIds.Hash(id).ShouldBe(Convert.ToHexStringLower(SHA1.HashData(Encoding.UTF8.GetBytes(id))));
    }
}
