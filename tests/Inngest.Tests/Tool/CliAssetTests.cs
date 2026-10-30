using System.Runtime.InteropServices;
using Inngest.Tool;
using Inngest.Tool.Download;

namespace Inngest.Tests.Tool;

public sealed class CliAssetTests
{
    // The names of the six files of the v1.49.0 release on GitHub.
    [Theory]
    [InlineData("Linux", Architecture.X64, "inngest_1.49.0_linux_amd64.tar.gz", "inngest", "TarGz")]
    [InlineData("Linux", Architecture.Arm64, "inngest_1.49.0_linux_arm64.tar.gz", "inngest", "TarGz")]
    [InlineData("OSX", Architecture.X64, "inngest_1.49.0_darwin_amd64.tar.gz", "inngest", "TarGz")]
    [InlineData("OSX", Architecture.Arm64, "inngest_1.49.0_darwin_arm64.tar.gz", "inngest", "TarGz")]
    [InlineData("Windows", Architecture.X64, "inngest_1.49.0_windows_amd64.zip", "inngest.exe", "Zip")]
    [InlineData("Windows", Architecture.Arm64, "inngest_1.49.0_windows_arm64.zip", "inngest.exe", "Zip")]
    public void Each_system_and_architecture_has_the_asset_the_release_names_for_it(
        string system, Architecture architecture, string name, string binary, string format)
    {
        var asset = CliAsset.Select("1.49.0", OSPlatform.Create(system), architecture);

        asset.Name.ShouldBe(name);
        asset.BinaryName.ShouldBe(binary);
        asset.Format.ToString().ShouldBe(format);
    }

    [Fact]
    public void The_platform_names_the_folder_of_the_cache_and_leaves_out_the_version()
    {
        CliAsset.Select("1.49.0", OSPlatform.OSX, Architecture.Arm64).Platform.ShouldBe("darwin_arm64");
        CliAsset.Select("1.49.0", OSPlatform.Windows, Architecture.X64).Platform.ShouldBe("windows_amd64");
    }

    [Fact]
    public void The_version_is_part_of_the_name()
    {
        CliAsset.Select("1.50.0-beta.2", OSPlatform.Linux, Architecture.X64).Name.ShouldBe("inngest_1.50.0-beta.2_linux_amd64.tar.gz");
    }

    [Theory]
    [InlineData("Linux", Architecture.Arm)]
    [InlineData("Linux", Architecture.X86)]
    [InlineData("Linux", Architecture.S390x)]
    [InlineData("Windows", Architecture.X86)]
    [InlineData("FreeBSD", Architecture.X64)]
    public void A_system_without_a_release_is_refused_with_the_way_to_run_the_dev_server_in_docker(string system, Architecture architecture)
    {
        var exception = Should.Throw<ToolException>(() => CliAsset.Select("1.49.0", OSPlatform.Create(system), architecture));

        exception.Message.ShouldContain("no release");
        exception.SuggestsDocker.ShouldBeTrue();
    }

    [Fact]
    public void The_machine_this_runs_on_has_an_asset()
    {
        var asset = CliAsset.Current("1.49.0");

        asset.Name.ShouldStartWith("inngest_1.49.0_");
        asset.BinaryName.ShouldBe(OperatingSystem.IsWindows() ? "inngest.exe" : "inngest");
    }

    [Theory]
    [InlineData("1.49.0", "1.49.0")]
    [InlineData("v1.49.0", "1.49.0")]
    [InlineData("1.50.0-beta.1", "1.50.0-beta.1")]
    public void A_release_version_is_taken_with_or_without_the_v_of_its_tag(string requested, string expected)
    {
        CliRelease.TryNormalizeVersion(requested, out var version).ShouldBeTrue();
        version.ShouldBe(expected);
    }

    // The version ends up in a URL and in the name of a folder.
    [Theory]
    [InlineData("")]
    [InlineData("latest")]
    [InlineData("1.49")]
    [InlineData("1.49.0/../../x")]
    [InlineData("..")]
    [InlineData("1.49.0 ")]
    [InlineData("1.49.0?x=1")]
    public void Anything_else_is_not_a_version(string requested)
    {
        CliRelease.TryNormalizeVersion(requested, out var version).ShouldBeFalse();
        version.ShouldBeNull();
    }

    [Fact]
    public void Files_are_downloaded_from_the_release_of_the_tag()
    {
        CliRelease.AssetUri("1.49.0", "inngest_1.49.0_linux_amd64.tar.gz").ToString()
            .ShouldBe("https://github.com/inngest/inngest/releases/download/v1.49.0/inngest_1.49.0_linux_amd64.tar.gz");
        CliRelease.ChecksumsUri("1.49.0").ToString()
            .ShouldBe("https://github.com/inngest/inngest/releases/download/v1.49.0/checksums.txt");
    }
}
