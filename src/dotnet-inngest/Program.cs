// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE in the repository root.

using Inngest.Tool.Download;

namespace Inngest.Tool;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        using CancellationTokenSource cancellation = new();

        // In a terminal Ctrl+C reaches the Inngest CLI too, and it shuts down. The tool must stay until it has, so that
        // the exit code is the CLI's, and until then it only ends a download.
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cancellation.Cancel();
        };

        using HttpClient http = new(new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(30) })
        {
            // The archive is 30 MB, and a slow connection is no reason to give up on it after the 100 seconds of the default.
            Timeout = TimeSpan.FromMinutes(10),
        };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("dotnet-inngest");

        try
        {
            CliInstaller installer = new(http, CacheRoot(), Console.Error);
            ToolApp app = new(Console.Out, Console.Error, installer, CliAsset.Current, ChildProcess.RunAsync, Environment.CurrentDirectory);
            return await app.RunAsync(args, cancellation.Token);
        }
        catch (ToolException exception)
        {
            Console.Error.WriteLine($"error: {exception.Message}");
            return 1;
        }
    }

    private static string CacheRoot()
    {
        var configured = Environment.GetEnvironmentVariable("INNGEST_DOTNET_CACHE");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return Path.GetFullPath(configured);
        }

        var data = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return data.Length > 0
            ? Path.Combine(data, "inngest-dotnet", "cli")
            : throw new ToolException("The user has no local application data folder. Set INNGEST_DOTNET_CACHE to a folder for the Inngest CLI.");
    }
}
