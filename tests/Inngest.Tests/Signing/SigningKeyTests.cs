using Inngest.Signing;

namespace Inngest.Tests.Signing;

public sealed class SigningKeyTests
{
    [Fact]
    public void Bearer_token_keeps_the_prefix_and_hashes_the_hex_decoded_secret()
    {
        // The example from the SDK spec, section 4.1.4.
        var key = SigningKey.Create("signkey-prod-12345678")!;

        key.BearerToken.ShouldBe("signkey-prod-b2ed992186a5cb19f6668aade821f502c1d00970dfd0e35128d51bac4649916c");
    }

    [Fact]
    public void Keys_without_a_prefix_are_hashed_whole()
    {
        var key = SigningKey.Create("12345678")!;

        key.BearerToken.ShouldBe("b2ed992186a5cb19f6668aade821f502c1d00970dfd0e35128d51bac4649916c");
        key.HmacKey.ShouldBe("12345678"u8.ToArray());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Blank_keys_are_treated_as_missing(string? value) => SigningKey.Create(value).ShouldBeNull();

    [Fact]
    public void Event_keys_hash_as_utf8_text() =>
        SigningKey.HashEventKey("my-event-key").ShouldBe("756db3455bd70816c089ed7fcd514ab0e66cf5cdf62ccb7a3bca66d96ee3132c");
}
