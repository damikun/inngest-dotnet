using Inngest.Encryption;

namespace Inngest.Tests.Encryption;

public sealed class EncryptionKeysTests
{
    // Every character String.prototype.trim removes in Node 26, found by trimming each of the 65,536.
    private static readonly char[] Trimmed =
    [
        .. new char[] { (char)0x09, (char)0x0A, (char)0x0B, (char)0x0C, (char)0x0D, (char)0x20, (char)0xA0, (char)0x1680 },
        .. Enumerable.Range(0x2000, 11).Select(code => (char)code),
        .. new char[] { (char)0x2028, (char)0x2029, (char)0x202F, (char)0x205F, (char)0x3000, (char)0xFEFF },
    ];

    [Fact]
    public void The_key_comes_first_and_the_fallbacks_follow_in_order()
    {
        EncryptionKeys.Normalize("current", ["old", "older"]).ShouldBe(["current", "old", "older"]);
    }

    [Fact]
    public void Blank_keys_are_dropped_and_the_rest_are_kept()
    {
        EncryptionKeys.Normalize(null, null).ShouldBeEmpty();
        EncryptionKeys.Normalize("", [" ", "\t\n"]).ShouldBeEmpty();
        EncryptionKeys.Normalize("current", ["", " old ", null!, "  "]).ShouldBe(["current", "old"]);
    }

    [Fact]
    public void White_space_around_a_key_is_not_part_of_it_but_white_space_inside_is()
    {
        EncryptionKeys.Normalize("\n  two words \r\n", null).ShouldBe(["two words"]);
    }

    [Fact]
    public void Every_character_that_javascript_trims_is_trimmed()
    {
        foreach (var character in Trimmed)
        {
            EncryptionKeys.Normalize($"{character}key{character}", null).ShouldBe(["key"], $"for U+{(int)character:X4}");
        }
    }

    [Fact]
    public void Characters_that_javascript_keeps_are_kept_even_when_dotnet_treats_them_as_white_space()
    {
        // U+0085 is white space to Char.IsWhiteSpace, and U+180E and U+200B are separators that JavaScript ignores.
        foreach (var character in new[] { (char)0x85, (char)0x180E, (char)0x200B, (char)0x2060 })
        {
            EncryptionKeys.Normalize($"{character}key{character}", null).ShouldBe([$"{character}key{character}"], $"for U+{(int)character:X4}");
        }
    }
}
