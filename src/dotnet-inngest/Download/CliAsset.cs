// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.

using System.Runtime.InteropServices;

namespace Inngest.Tool.Download;

internal enum ArchiveFormat
{
    TarGz,
    Zip,
}

// One file of a release. GoReleaser names it inngest_<version>_<os>_<arch>, with .zip for Windows and .tar.gz for the
// rest, and the archive holds the binary at its top. The CLI is a static Go binary, so there is one asset per system and
// no variant for a C library.
internal sealed record CliAsset(string Name, ArchiveFormat Format, string BinaryName, string Platform)
{
    public static CliAsset Current(string version) => Select(version, CurrentSystem(), RuntimeInformation.OSArchitecture);

    // The architecture of the machine rather than of this process. Under Rosetta, or on Windows on Arm, the machine's
    // binary is the one that runs natively.
    public static CliAsset Select(string version, OSPlatform system, Architecture architecture)
    {
        var os = system == OSPlatform.Windows ? "windows"
            : system == OSPlatform.OSX ? "darwin"
            : system == OSPlatform.Linux ? "linux"
            : null;
        var arch = architecture switch
        {
            Architecture.X64 => "amd64",
            Architecture.Arm64 => "arm64",
            _ => null,
        };

        if (os is null || arch is null)
        {
            throw new ToolException($"The Inngest CLI has no release for {system} on {architecture}. It is released for Linux, macOS and Windows on x64 and Arm64.")
            {
                SuggestsDocker = true,
            };
        }

        var windows = os == "windows";
        return new CliAsset(
            $"inngest_{version}_{os}_{arch}{(windows ? ".zip" : ".tar.gz")}",
            windows ? ArchiveFormat.Zip : ArchiveFormat.TarGz,
            windows ? "inngest.exe" : "inngest",
            $"{os}_{arch}");
    }

    private static OSPlatform CurrentSystem() =>
        OperatingSystem.IsWindows() ? OSPlatform.Windows
        : OperatingSystem.IsMacOS() ? OSPlatform.OSX
        : OperatingSystem.IsLinux() ? OSPlatform.Linux
        : OSPlatform.Create(RuntimeInformation.RuntimeIdentifier);
}
