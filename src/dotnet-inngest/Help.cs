// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.

using Inngest.Tool.Release;

namespace Inngest.Tool;

internal static class Help
{
    public static string Text => $"""
        dotnet inngest runs the Inngest dev server for an app built with the Inngest .NET SDK.

        Usage:
          dotnet inngest dev [options] [-- <options of inngest dev>]
          dotnet inngest --version
          dotnet inngest --help

        dev downloads the Inngest CLI on first use, checks its SHA-256 and runs 'inngest dev'. The CLI is release
        {CliRelease.PinnedVersion} unless --cli-version names another, and is kept per user, so later runs download nothing.
        Options that dev does not know go to 'inngest dev' as they are, as in 'dotnet inngest dev --port 9000'.
        'dotnet inngest dev -- --help' shows the options of the CLI.

        Options of dev:
          -u, --sdk-url <url>     The URL of the app's Inngest endpoint, which the dev server calls. Can be given more than
                                  once. Without it, and without --no-discovery, the tool reads the app's address from
                                  Properties/launchSettings.json and adds /api/inngest.
          --project <path>        The folder or project file whose launch settings give that address. The current folder
                                  by default.
          --cli-version <version> Run another release of the Inngest CLI, such as 1.50.0.
          -h, --help              Show this help.

        Environment:
          INNGEST_DOTNET_CACHE    The folder the CLI is kept in. The default is inngest-dotnet/cli in the user's local
                                  application data.
        """ + Environment.NewLine;
}
