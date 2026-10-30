using Inngest.Encryption;

namespace Inngest.Tests.Encryption;

// The primitives, against what libsodium-wrappers 0.7.13 returned for the same inputs.
public sealed class SecretBoxTests
{
    [Fact]
    public void The_key_is_the_blake2b_hash_of_the_passphrase_as_libsodiums_generichash_makes_it()
    {
        foreach (var (_, trimmed, hash) in JsVectors.KeyHashes)
        {
            Hex(SecretBox.DeriveKey(trimmed)).ShouldBe(hash, $"for {trimmed}");
        }
    }

    [Fact]
    public void A_box_is_what_crypto_secretbox_easy_makes_across_the_blocks_of_the_stream()
    {
        var key = SecretBox.DeriveKey(JsVectors.Key);
        var nonce = Convert.FromHexString(JsVectors.BoxNonce);

        foreach (var (length, expected) in JsVectors.Boxes)
        {
            var box = new byte[SecretBox.TagSize + length];
            SecretBox.Seal(key, nonce, Message(length), box);

            Hex(box).ShouldBe(expected, $"for {length} bytes");
        }
    }

    [Fact]
    public void A_box_from_crypto_secretbox_easy_opens_to_its_message()
    {
        var key = SecretBox.DeriveKey(JsVectors.Key);
        var nonce = Convert.FromHexString(JsVectors.BoxNonce);

        foreach (var (length, box) in JsVectors.Boxes)
        {
            var message = new byte[length];

            SecretBox.TryOpen(key, nonce, Convert.FromHexString(box), message).ShouldBeTrue($"for {length} bytes");
            message.ShouldBe(Message(length));
        }
    }

    [Fact]
    public void A_box_changed_anywhere_is_not_opened_and_nothing_is_decrypted()
    {
        var key = SecretBox.DeriveKey(JsVectors.Key);
        var nonce = Convert.FromHexString(JsVectors.BoxNonce);
        var box = Convert.FromHexString(JsVectors.Boxes[^1].Box);

        foreach (var position in new[] { 0, SecretBox.TagSize - 1, SecretBox.TagSize, box.Length - 1 })
        {
            var changed = (byte[])box.Clone();
            changed[position] ^= 1;
            var message = new byte[box.Length - SecretBox.TagSize];

            SecretBox.TryOpen(key, nonce, changed, message).ShouldBeFalse($"with byte {position} changed");
            message.ShouldAllBe(value => value == 0);
        }
    }

    [Fact]
    public void A_box_is_not_opened_with_another_key_or_nonce()
    {
        var key = SecretBox.DeriveKey(JsVectors.Key);
        var nonce = Convert.FromHexString(JsVectors.BoxNonce);
        var box = Convert.FromHexString(JsVectors.Boxes[^1].Box);
        var message = new byte[box.Length - SecretBox.TagSize];

        SecretBox.TryOpen(SecretBox.DeriveKey(JsVectors.SecondKey), nonce, box, message).ShouldBeFalse();
        SecretBox.TryOpen(key, new byte[SecretBox.NonceSize], box, message).ShouldBeFalse();
    }

    [Fact]
    public void A_box_shorter_than_its_tag_is_not_opened()
    {
        var key = SecretBox.DeriveKey(JsVectors.Key);

        SecretBox.TryOpen(key, new byte[SecretBox.NonceSize], new byte[SecretBox.TagSize - 1], []).ShouldBeFalse();
    }

    // The message the vectors were made of.
    private static byte[] Message(int length)
    {
        var message = new byte[length];
        for (var i = 0; i < length; i++)
        {
            message[i] = (byte)(i * 31 + 7);
        }

        return message;
    }

    private static string Hex(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();
}
