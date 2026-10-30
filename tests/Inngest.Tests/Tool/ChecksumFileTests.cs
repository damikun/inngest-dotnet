using System.Runtime.InteropServices;
using Inngest.Tool;
using Inngest.Tool.Release;

namespace Inngest.Tests.Tool;

public sealed class ChecksumFileTests
{
    private const string Hash = "190f0a3c433a73546cd346c4638ff9027a94af77ef581357de90715c6d739c70";
    private const string OtherHash = "f60d2a9200a5a037f3e24fc7fc19df45200551510d7a81d77eed03cfe87f3e94";

    [Fact]
    public void The_checksums_of_a_release_are_read_by_file_name()
    {
        var checksums = ChecksumFile.Parse($"{Hash}  inngest_1.49.0_darwin_amd64.tar.gz\n{OtherHash}  inngest_1.49.0_darwin_arm64.tar.gz\n");

        checksums.TryGet("inngest_1.49.0_darwin_amd64.tar.gz", out var amd64).ShouldBeTrue();
        amd64.ShouldBe(Hash);
        checksums.TryGet("inngest_1.49.0_darwin_arm64.tar.gz", out var arm64).ShouldBeTrue();
        arm64.ShouldBe(OtherHash);
        checksums.TryGet("inngest_1.49.0_linux_amd64.tar.gz", out var missing).ShouldBeFalse();
        missing.ShouldBeNull();
    }

    [Fact]
    public void Windows_line_endings_blank_lines_and_the_binary_mode_marker_are_accepted()
    {
        var checksums = ChecksumFile.Parse($"\r\n{Hash} *inngest.zip\r\n\r\n{OtherHash}\tinngest.tar.gz\r\n");

        checksums.TryGet("inngest.zip", out var zip).ShouldBeTrue();
        zip.ShouldBe(Hash);
        checksums.TryGet("inngest.tar.gz", out var tar).ShouldBeTrue();
        tar.ShouldBe(OtherHash);
    }

    [Fact]
    public void A_hash_is_compared_in_lower_case()
    {
        ChecksumFile.Parse($"{Hash.ToUpperInvariant()}  inngest.zip").TryGet("inngest.zip", out var hash).ShouldBeTrue();

        hash.ShouldBe(Hash);
    }

    [Theory]
    [InlineData("<html><body>Not Found</body></html>")]
    [InlineData("190f0a3c433a73546cd346c4638ff9027a94af77ef581357de90715c6d739c  too-short.tar.gz")]
    [InlineData("190f0a3c433a73546cd346c4638ff9027a94af77ef581357de90715c6d739c700  too-long.tar.gz")]
    [InlineData("g90f0a3c433a73546cd346c4638ff9027a94af77ef581357de90715c6d739c70  not-hex.tar.gz")]
    [InlineData("190f0a3c433a73546cd346c4638ff9027a94af77ef581357de90715c6d739c70")]
    [InlineData("190f0a3c433a73546cd346c4638ff9027a94af77ef581357de90715c6d739c70   ")]
    [InlineData("190f0a3c433a73546cd346c4638ff9027a94af77ef581357de90715c6d739c70:inngest.tar.gz")]
    public void A_line_that_is_not_a_hash_and_a_name_fails_the_whole_file(string text)
    {
        // The first line is good, so that a file that was only partly read cannot pass.
        Should.Throw<ToolException>(() => ChecksumFile.Parse($"{Hash}  inngest.zip\n{text}\n")).Message.ShouldStartWith("Line 2");
    }

    [Fact]
    public void A_file_listed_twice_with_different_hashes_is_ambiguous()
    {
        Should.Throw<ToolException>(() => ChecksumFile.Parse($"{Hash}  inngest.zip\n{OtherHash}  inngest.zip\n"))
            .Message.ShouldContain("twice");
        ChecksumFile.Parse($"{Hash}  inngest.zip\n{Hash}  inngest.zip\n").TryGet("inngest.zip", out _).ShouldBeTrue();
    }

    [Fact]
    public void An_empty_file_lists_nothing()
    {
        ChecksumFile.Parse("").TryGet("inngest.zip", out _).ShouldBeFalse();
    }

    // The committed checksums are what protects a download of the pinned version, so a release that is pinned without
    // all of its files, or with a file that is no hash, must not get through the build.
    [Fact]
    public void The_pinned_release_has_a_checksum_for_the_asset_of_every_system()
    {
        foreach (var (system, architecture) in new[]
        {
            (OSPlatform.Linux, Architecture.X64), (OSPlatform.Linux, Architecture.Arm64),
            (OSPlatform.OSX, Architecture.X64), (OSPlatform.OSX, Architecture.Arm64),
            (OSPlatform.Windows, Architecture.X64), (OSPlatform.Windows, Architecture.Arm64),
        })
        {
            var asset = CliAsset.Select(CliRelease.PinnedVersion, system, architecture);

            PinnedChecksums.Checksums.TryGet(asset.Name, out var hash).ShouldBeTrue(asset.Name);
            hash!.Length.ShouldBe(64);
        }
    }

    [Fact]
    public void The_pinned_checksums_list_the_release_and_nothing_else()
    {
        PinnedChecksums.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries).ShouldAllBe(line => line.Contains($"_{CliRelease.PinnedVersion}_"));
        PinnedChecksums.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length.ShouldBe(6);
    }
}
