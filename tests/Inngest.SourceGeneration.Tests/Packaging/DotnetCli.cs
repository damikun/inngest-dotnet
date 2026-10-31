using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace Inngest.SourceGeneration.Tests.Packaging;

// Runs the dotnet command line the way a developer does, to build what the tests pack.
internal static partial class DotnetCli
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(8);

    public static string RepositoryRoot { get; } = FindRepositoryRoot();

    public static async Task<(int ExitCode, string Output)> RunAsync(string workingDirectory, params string[] arguments)
    {
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } host ? host : "dotnet")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        // The test host runs under one SDK, and the variables its command line sets for it would pin every dotnet it starts to
        // that SDK, whatever global.json says.
        foreach (var name in start.Environment.Keys.Where(static key => key.StartsWith("MSBUILD", StringComparison.OrdinalIgnoreCase)).ToList())
        {
            start.Environment.Remove(name);
        }

        start.Environment["DOTNET_NOLOGO"] = "1";
        start.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        start.Environment["MSBUILDDISABLENODEREUSE"] = "1";

        using var process = Process.Start(start) ?? throw new InvalidOperationException("dotnet did not start.");
        var output = new StringBuilder();
        void Collect(object sender, DataReceivedEventArgs received)
        {
            if (received.Data is not null)
            {
                lock (output)
                {
                    output.AppendLine(received.Data);
                }
            }
        }

        process.OutputDataReceived += Collect;
        process.ErrorDataReceived += Collect;
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeout = new CancellationTokenSource(Timeout);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"dotnet {string.Join(' ', arguments)} did not finish in {Timeout}.\n{output}");
        }

        // The last lines of output may still be on their way when the process has exited.
        process.WaitForExit();
        lock (output)
        {
            return (process.ExitCode, output.ToString());
        }
    }

    // The newest 8.0 SDK that is installed, or null.
    public static async Task<string?> FindSdk8Async()
    {
        var (exitCode, output) = await RunAsync(Path.GetTempPath(), "--list-sdks");
        if (exitCode != 0)
        {
            return null;
        }

        return Sdk8Pattern().Matches(output)
            .Select(static match => match.Groups[1].Value)
            .OrderByDescending(static version => Version.Parse(version.Split('-')[0]))
            .FirstOrDefault();
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Inngest.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("Inngest.slnx was not found above " + AppContext.BaseDirectory);
    }

    [GeneratedRegex(@"^(8\.0\.\d+)\s", RegexOptions.Multiline)]
    private static partial Regex Sdk8Pattern();
}
