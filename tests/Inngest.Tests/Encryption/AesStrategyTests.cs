using System.Security.Cryptography;
using System.Text;
using Inngest.Encryption;

namespace Inngest.Tests.Encryption;

// The AES that crypto-js writes, which is what the first versions of the TypeScript package encrypted with and what its
// AESEncryptionService still does. It is read here and never written.
public sealed class AesStrategyTests
{
    public static TheoryData<string> V0Names => new(JsVectors.AesV0.Select(vector => vector.Name));

    public static TheoryData<string> StrategyNames => new(JsVectors.AesStrategy.Select(vector => vector.Name));

    public static TheoryData<string> OtherKeyNames => new(JsVectors.AesOtherKeys.Select(vector => vector.Name));

    [Theory]
    [MemberData(nameof(V0Names))]
    public void It_decrypts_what_the_v0_envelopes_of_the_typescript_package_hold(string name)
    {
        var vector = JsVectors.Find(JsVectors.AesV0, name);

        JsVectors.Parse(vector.Envelope).TryGetProperty("__STRATEGY__", out _).ShouldBeFalse();
        Decrypt(vector.Envelope, vector.Key).ShouldBe(vector.Plaintext);
    }

    [Theory]
    [MemberData(nameof(StrategyNames))]
    public void It_decrypts_what_its_own_strategy_of_the_typescript_package_encrypted(string name)
    {
        var vector = JsVectors.Find(JsVectors.AesStrategy, name);

        JsVectors.Parse(vector.Envelope).GetProperty("__STRATEGY__").GetString().ShouldBe(AesDecryptor.StrategyName);
        Decrypt(vector.Envelope, vector.Key).ShouldBe(vector.Plaintext);
    }

    [Theory]
    [MemberData(nameof(OtherKeyNames))]
    public void It_derives_the_key_from_the_trimmed_utf8_text_of_the_passphrase(string name)
    {
        var vector = JsVectors.Find(JsVectors.AesOtherKeys, name);

        Decrypt(vector.Envelope, vector.Key).ShouldBe(vector.Plaintext);
    }

    [Fact]
    public void It_falls_back_to_the_next_key_when_a_key_does_not_decrypt()
    {
        Decrypt(JsVectors.SecondKeyAesEnvelope, JsVectors.Key, JsVectors.SecondKey).ShouldBe("""{"rotated":true}""");
    }

    [Fact]
    public void It_fails_without_a_key_that_decrypts()
    {
        var failure = Should.Throw<CryptographicException>(() => Decrypt(JsVectors.AesV0[0].Envelope, "not-the-key", "nor-this-one"));

        failure.Message.ShouldBe("The encrypted value could not be decrypted with any of the configured keys.");
    }

    [Fact]
    public void It_fails_on_data_that_is_not_base64_or_was_not_salted_or_is_cut_short()
    {
        var data = Convert.FromBase64String(EncryptionHarness.DataOf(JsVectors.AesV0[0].Envelope));

        Should.Throw<CryptographicException>(() => Decrypt("""{"__ENCRYPTED__":true,"data":"not base64!"}""", JsVectors.Key));
        Should.Throw<CryptographicException>(() => Decrypt(Envelope(data[8..]), JsVectors.Key));
        Should.Throw<CryptographicException>(() => Decrypt(Envelope(data[..^1]), JsVectors.Key));
        Should.Throw<CryptographicException>(() => Decrypt(Envelope(data[..16]), JsVectors.Key));
    }

    // AES-CBC has no authentication, so text that decrypts to something other than JSON is as good as a wrong key.
    [Fact]
    public void It_does_not_accept_a_key_whose_text_is_not_json()
    {
        var data = Convert.FromBase64String(EncryptionHarness.DataOf(JsVectors.AesV0[0].Envelope));
        data[^17] ^= 0x55;

        Should.Throw<CryptographicException>(() => Decrypt(Envelope(data), JsVectors.Key));
    }

    private static string Envelope(byte[] data) => $$"""{"__ENCRYPTED__":true,"data":"{{Convert.ToBase64String(data)}}"}""";

    private static string Decrypt(string envelope, string key, params string[] fallbacks) =>
        Encoding.UTF8.GetString(new AesDecryptor(EncryptionKeys.Normalize(key, fallbacks)).Decrypt(EncryptionHarness.DataOf(envelope)));
}
