// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.

using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace Inngest.Tool.Download;

// Where the Inngest CLI comes from: the GitHub releases of inngest/inngest, the same files the install script and the
// inngest-cli npm package download.
internal static partial class CliRelease
{
    // The version the tool runs unless --cli-version names another. It is the version of the inngest/inngest image the
    // repository's tests and docs use. Raise them together, and replace PinnedChecksums.Text with the new release's
    // checksums.txt.
    public const string PinnedVersion = "1.49.0";

    // Release versions are semantic versions, which the tag spells with a leading v. The version ends up in a URL and in
    // a folder name, so anything else is refused.
    public static bool TryNormalizeVersion(string requested, [NotNullWhen(true)] out string? version)
    {
        version = requested.StartsWith('v') ? requested[1..] : requested;
        if (VersionPattern().IsMatch(version))
        {
            return true;
        }

        version = null;
        return false;
    }

    public static Uri AssetUri(string version, string fileName) =>
        new($"https://github.com/inngest/inngest/releases/download/v{version}/{fileName}");

    public static Uri ChecksumsUri(string version) => AssetUri(version, ChecksumsFileName);

    public const string ChecksumsFileName = "checksums.txt";

    [GeneratedRegex(@"^\d+\.\d+\.\d+(-[0-9A-Za-z]+(\.[0-9A-Za-z]+)*)?$")]
    private static partial Regex VersionPattern();
}
