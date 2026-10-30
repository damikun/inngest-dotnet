// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.

namespace Inngest.Tool.Download;

// Where the releases of the Inngest CLI are kept. Per user, so that every project on the machine shares one download.
internal static class CliCache
{
    public const string RootVariable = "INNGEST_DOTNET_CACHE";

    // The local application data folder is where NuGet keeps its http cache too: %LOCALAPPDATA% on Windows, and on Linux
    // and macOS $XDG_DATA_HOME or ~/.local/share. It is empty when the user has no home folder, as in some services.
    public static string Root(string? configured, string localApplicationData)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return Path.GetFullPath(configured);
        }

        return localApplicationData.Length > 0
            ? Path.Combine(localApplicationData, "inngest-dotnet", "cli")
            : throw new ToolException($"The user has no local application data folder. {RootVariable} names a folder for the Inngest CLI.");
    }
}
