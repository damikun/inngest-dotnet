using System.Security.Cryptography;
using System.Text;
using Inngest.Encryption;

namespace Inngest.Tests.Encryption;

// The default strategy, inngest/libsodium, in both directions with the TypeScript package.
public sealed class LibSodiumStrategyTests
{
    public static TheoryData<string> Names => new(JsVectors.LibSodium.Select(vector => vector.Name));

    public static TheoryData<string> OtherKeyNames => new(JsVectors.OtherKeys.Select(vector => vector.Name));

    [Theory]
    [MemberData(nameof(Names))]
    public void It_decrypts_what_the_typescript_package_encrypted(string name)
    {
        var vector = JsVectors.Find(JsVectors.LibSodium, name);

        JsVectors.Parse(vector.Envelope).GetProperty("__STRATEGY__").GetString().ShouldBe("inngest/libsodium");
        EncryptionHarness.Decrypt(Service(vector.Key), vector.Envelope).ShouldBe(vector.Plaintext);
    }

    [Theory]
    [MemberData(nameof(OtherKeyNames))]
    public void It_uses_keys_as_the_typescript_package_does_with_surrounding_white_space_removed_and_utf8_text(string name)
    {
        var vector = JsVectors.Find(JsVectors.OtherKeys, name);

        EncryptionHarness.Decrypt(Service(vector.Key), vector.Envelope).ShouldBe(vector.Plaintext);
    }

    [Fact]
    public void It_falls_back_to_the_next_key_when_a_key_does_not_decrypt()
    {
        var service = Service(JsVectors.Key, JsVectors.SecondKey);

        EncryptionHarness.Decrypt(service, JsVectors.SecondKeyEnvelope).ShouldBe("""{"rotated":true}""");
        EncryptionHarness.Decrypt(service, JsVectors.LibSodium[0].Envelope).ShouldBe(JsVectors.LibSodium[0].Plaintext);
    }

    [Fact]
    public void It_fails_without_a_key_that_decrypts_and_does_not_say_what_the_keys_are()
    {
        var service = Service("not-the-key", "nor-this-one");

        var failure = Should.Throw<CryptographicException>(() => service.Decrypt(EncryptionHarness.DataOf(JsVectors.LibSodium[0].Envelope)));

        failure.Message.ShouldBe("The encrypted value could not be decrypted with any of the configured keys.");
    }

    [Fact]
    public void It_fails_on_data_that_is_not_base64_or_too_short_for_a_nonce_and_a_tag()
    {
        var service = Service(JsVectors.Key);

        Should.Throw<CryptographicException>(() => service.Decrypt("not base64!")).Message.ShouldContain("not valid base64");
        Should.Throw<CryptographicException>(() => service.Decrypt(Convert.ToBase64String(new byte[39]))).Message.ShouldContain("too short");
    }

    [Fact]
    public void It_fails_on_data_that_was_changed()
    {
        var service = Service(JsVectors.Key);
        var data = Convert.FromBase64String(EncryptionHarness.DataOf(JsVectors.LibSodium[0].Envelope));
        data[^1] ^= 1;

        Should.Throw<CryptographicException>(() => service.Decrypt(Convert.ToBase64String(data)));
    }

    [Fact]
    public void It_encrypts_into_a_nonce_followed_by_a_box_and_a_nonce_is_never_used_twice()
    {
        var service = Service(JsVectors.Key);
        var json = Encoding.UTF8.GetBytes("""{"hello":"world"}""");

        var first = service.Encrypt(json);
        var second = service.Encrypt(json);

        first.ShouldNotBe(second);
        Convert.FromBase64String(first).Length.ShouldBe(SecretBox.NonceSize + SecretBox.TagSize + json.Length);
        service.Decrypt(first).ShouldBe(json);
        service.Decrypt(second).ShouldBe(json);
    }

    [Fact]
    public void It_encrypts_with_the_first_key_only()
    {
        var json = Encoding.UTF8.GetBytes("1");
        var data = Service(JsVectors.SecondKey, JsVectors.Key).Encrypt(json);

        Service(JsVectors.SecondKey).Decrypt(data).ShouldBe(json);
        Should.Throw<CryptographicException>(() => Service(JsVectors.Key).Decrypt(data));
    }

    [Fact]
    public void It_encrypts_and_decrypts_text_of_every_kind_and_size()
    {
        var service = Service(JsVectors.Key);

        foreach (var length in new[] { 0, 1, 31, 32, 33, 63, 64, 65, 1000, 100_000 })
        {
            var json = new byte[length];
            RandomNumberGenerator.Fill(json);

            service.Decrypt(service.Encrypt(json)).ShouldBe(json, $"for {length} bytes");
        }
    }

    private static LibSodiumEncryptionService Service(string key, params string[] fallbacks) =>
        new(EncryptionKeys.Normalize(key, fallbacks));
}
