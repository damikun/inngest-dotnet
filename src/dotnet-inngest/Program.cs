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
            // The installer limits how long the server may send nothing. A limit on the whole download would cut off a slow
            // connection that is alive, and with ResponseHeadersRead the Timeout of the client would end at the headers.
            Timeout = Timeout.InfiniteTimeSpan,
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

    private static string CacheRoot() => CliCache.Root(
        Environment.GetEnvironmentVariable(CliCache.RootVariable),
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
}
